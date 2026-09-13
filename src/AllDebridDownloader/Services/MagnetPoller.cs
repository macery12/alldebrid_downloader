using AllDebridDownloader.Models;

namespace AllDebridDownloader.Services;

/// <summary>
/// Single polling loop for every tracked magnet.
///
/// Uses the documented live mode: a fixed random session id plus a counter, where the
/// first call returns everything with fullsync=true and later calls return only the
/// fields that changed. The full state is held here and deltas are applied onto it.
/// Falls back to plain full-status polling if live mode misbehaves.
/// </summary>
public sealed class MagnetPoller : IDisposable
{
    private readonly AllDebridClient _client;
    private readonly Logger _log;
    private readonly ConfigService _config;

    private readonly Dictionary<long, MagnetStatus> _state = new();
    private readonly object _sync = new();

    private readonly long _session = Random.Shared.NextInt64(1, int.MaxValue);
    private long _counter;
    private bool _liveModeUsable = true;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MagnetPoller(AllDebridClient client, Logger log, ConfigService config)
    {
        _client = client;
        _log = log;
        _config = config;
    }

    /// <summary>Raised with the full current state after each successful poll.</summary>
    public event Action<IReadOnlyList<MagnetStatus>>? StatusUpdated;

    /// <summary>Raised when a magnet reaches statusCode 4 (Ready).</summary>
    public event Action<MagnetStatus>? MagnetBecameReady;

    /// <summary>Raised with a user-facing note about connectivity, or null to clear it.</summary>
    public event Action<string?>? ConnectionNotice;

    /// <summary>True while live mode is in use rather than full-status polling.</summary>
    public bool IsLiveMode => _liveModeUsable;

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _log.Info("Poller", "Status polling started (live mode, session " + _session + ").");
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop is not null) await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>Nudge the loop to poll immediately, e.g. after adding a magnet.</summary>
    public void RequestImmediatePoll() => _wakeUp.Set();

    private readonly AsyncAutoResetEvent _wakeUp = new();

    private async Task LoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);

                if (consecutiveFailures > 0)
                {
                    consecutiveFailures = 0;
                    ConnectionNotice?.Invoke(null);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AllDebridApiException ex) when (ex.IsAuthFailure)
            {
                // No point hammering an endpoint that will keep rejecting us.
                ConnectionNotice?.Invoke(ex.Message);
                _log.Error("Poller", "Polling stopped: " + ex.Message);
                return;
            }
            catch (AllDebridApiException ex)
            {
                consecutiveFailures++;
                ConnectionNotice?.Invoke(consecutiveFailures > 1
                    ? "Reconnecting to AllDebrid…"
                    : ex.Message);
                _log.Warn("Poller", "Poll failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                ConnectionNotice?.Invoke("Reconnecting to AllDebrid…");
                _log.Warn("Poller", "Poll failed unexpectedly: " + ex.Message);
            }

            var interval = TimeSpan.FromSeconds(_config.Current.StatusPollIntervalSeconds);

            // Back off when the API is unhappy rather than retrying on the normal cadence.
            if (consecutiveFailures > 0)
            {
                var backoff = Math.Min(30, interval.TotalSeconds * Math.Pow(2, consecutiveFailures - 1));
                interval = TimeSpan.FromSeconds(backoff);
            }
            else if (!HasWork())
            {
                // Nothing in flight: idle slowly instead of polling every 3 seconds.
                interval = TimeSpan.FromSeconds(Math.Max(15, interval.TotalSeconds * 5));
            }

            await _wakeUp.WaitAsync(interval, ct).ConfigureAwait(false);
        }
    }

    private bool HasWork()
    {
        lock (_sync)
            return _state.Values.Any(m => m.Kind is MagnetStatusKind.Processing or MagnetStatusKind.Unknown);
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        MagnetStatusEnvelope envelope;

        if (_liveModeUsable)
        {
            try
            {
                envelope = await _client.GetStatusLiveAsync(_session, _counter, ct).ConfigureAwait(false);
            }
            catch (AllDebridApiException ex) when (!ex.IsAuthFailure)
            {
                // Live mode is an optimisation, not a requirement. If the API will not
                // play along, drop to full status and say so rather than failing.
                _log.Warn("Poller", "Live mode failed (" + ex.Message
                    + "); falling back to full status polling.");
                _liveModeUsable = false;
                envelope = await _client.GetStatusAsync(ct: ct).ConfigureAwait(false);
            }
        }
        else
        {
            envelope = await _client.GetStatusAsync(ct: ct).ConfigureAwait(false);
        }

        var readyNow = new List<MagnetStatus>();

        lock (_sync)
        {
            var isFullSync = !_liveModeUsable || envelope.FullSync == true;

            if (isFullSync)
            {
                // A fullsync is the authoritative whole picture; drop anything stale.
                var seen = new HashSet<long>();
                foreach (var incoming in envelope.Magnets ?? new List<MagnetStatus>())
                {
                    seen.Add(incoming.Id);
                    ApplyLocked(incoming, readyNow);
                }

                foreach (var goneId in _state.Keys.Where(k => !seen.Contains(k)).ToList())
                    _state.Remove(goneId);
            }
            else
            {
                foreach (var delta in envelope.Magnets ?? new List<MagnetStatus>())
                    ApplyLocked(delta, readyNow);
            }

            if (envelope.Counter is not null) _counter = envelope.Counter.Value;
        }

        // A fullsync with a reset counter is normal recovery, not an error.
        if (envelope.FullSync == true && _counter > 1)
            _log.Debug("Poller", "Live mode resynchronised (counter reset).");

        List<MagnetStatus> snapshot;
        lock (_sync) snapshot = _state.Values.ToList();

        StatusUpdated?.Invoke(snapshot);

        foreach (var ready in readyNow)
        {
            _log.Info("Poller", "Magnet " + ready.Id + " is ready: " + (ready.Filename ?? "(no name)"));
            MagnetBecameReady?.Invoke(ready);
        }
    }

    /// <summary>Merge one incoming object into the held state. Caller holds _sync.</summary>
    private void ApplyLocked(MagnetStatus incoming, List<MagnetStatus> readyNow)
    {
        if (_state.TryGetValue(incoming.Id, out var existing))
        {
            var wasReady = existing.IsReady;
            existing.ApplyDelta(incoming);
            if (!wasReady && existing.IsReady) readyNow.Add(existing);
        }
        else
        {
            _state[incoming.Id] = incoming;
            if (incoming.IsReady) readyNow.Add(incoming);
        }
    }

    /// <summary>
    /// Add a magnet returned by an upload so it is tracked before the first poll and the
    /// user sees a row immediately.
    /// </summary>
    public void Track(UploadedMagnet uploaded)
    {
        lock (_sync)
        {
            if (_state.ContainsKey(uploaded.Id)) return;
            _state[uploaded.Id] = new MagnetStatus
            {
                Id = uploaded.Id,
                Filename = uploaded.Name,
                Size = uploaded.Size,
                StatusCode = uploaded.Ready ? 4 : 0,
                Status = uploaded.Ready ? "Ready" : "In queue"
            };
        }
        RequestImmediatePoll();
    }

    public void Forget(long magnetId)
    {
        lock (_sync) _state.Remove(magnetId);
    }

    public MagnetStatus? Get(long magnetId)
    {
        lock (_sync) return _state.TryGetValue(magnetId, out var m) ? m : null;
    }

    /// <summary>
    /// One-off full status fetch for the Refresh button, bypassing the live-mode state.
    /// </summary>
    public async Task RefreshNowAsync(CancellationToken ct = default)
    {
        var envelope = await _client.GetStatusAsync(ct: ct).ConfigureAwait(false);

        var readyNow = new List<MagnetStatus>();
        lock (_sync)
        {
            _state.Clear();
            foreach (var m in envelope.Magnets ?? new List<MagnetStatus>())
            {
                _state[m.Id] = m;
                if (m.IsReady) readyNow.Add(m);
            }
            // Force the next live call to resynchronise against this fresh state.
            _counter = 0;
        }

        List<MagnetStatus> snapshot;
        lock (_sync) snapshot = _state.Values.ToList();
        StatusUpdated?.Invoke(snapshot);

        foreach (var ready in readyNow) MagnetBecameReady?.Invoke(ready);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

/// <summary>
/// Auto-reset event that can be awaited with a timeout, so the poll loop can both sleep
/// on its interval and be woken early when a magnet is added.
/// </summary>
internal sealed class AsyncAutoResetEvent
{
    private readonly object _sync = new();
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _signalled;

    public void Set()
    {
        lock (_sync)
        {
            _signalled = true;
            _tcs.TrySetResult();
        }
    }

    /// <summary>Waits for a signal or the timeout, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task wait;
        lock (_sync)
        {
            if (_signalled)
            {
                _signalled = false;
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return;
            }
            wait = _tcs.Task;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var delay = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
        var completed = await Task.WhenAny(wait, delay).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        if (completed == wait)
        {
            lock (_sync)
            {
                _signalled = false;
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
