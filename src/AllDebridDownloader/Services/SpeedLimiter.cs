using System.Diagnostics;

namespace AllDebridDownloader.Services;

/// <summary>
/// Token bucket shared by every active segment, so the configured limit is a whole-app
/// ceiling rather than a per-connection one. A limit of 0 disables it entirely and
/// costs nothing on the hot path.
/// </summary>
public sealed class SpeedLimiter
{
    private readonly object _sync = new();
    private long _bytesPerSecond;
    private double _available;
    private long _lastRefillTicks;

    public SpeedLimiter(long bytesPerSecond = 0)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _available = _bytesPerSecond;
        _lastRefillTicks = Stopwatch.GetTimestamp();
    }

    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set
        {
            lock (_sync)
            {
                _bytesPerSecond = Math.Max(0, value);
                // Never carry a burst larger than one second of the new limit.
                _available = Math.Min(_available, _bytesPerSecond);
                _lastRefillTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    public bool IsEnabled => Interlocked.Read(ref _bytesPerSecond) > 0;

    /// <summary>
    /// Wait until <paramref name="wanted"/> bytes may be read, and return how many are
    /// actually permitted right now (never more than wanted, never less than 1).
    /// </summary>
    public async ValueTask<int> AcquireAsync(int wanted, CancellationToken ct)
    {
        if (!IsEnabled || wanted <= 0) return wanted;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan wait;
            lock (_sync)
            {
                Refill();

                if (_available >= 1)
                {
                    var granted = (int)Math.Min(wanted, _available);
                    _available -= granted;
                    return Math.Max(1, granted);
                }

                var deficit = 1 - _available;
                var rate = _bytesPerSecond <= 0 ? 1 : _bytesPerSecond;
                wait = TimeSpan.FromSeconds(deficit / rate);
            }

            if (wait < TimeSpan.FromMilliseconds(5)) wait = TimeSpan.FromMilliseconds(5);
            if (wait > TimeSpan.FromMilliseconds(250)) wait = TimeSpan.FromMilliseconds(250);
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastRefillTicks) / (double)Stopwatch.Frequency;
        if (elapsed <= 0) return;

        _lastRefillTicks = now;
        _available = Math.Min(_bytesPerSecond, _available + elapsed * _bytesPerSecond);
    }
}

/// <summary>
/// Throughput over a sliding window. A cumulative average makes a download look
/// steady when it has actually stalled, so the UI uses this instead.
/// </summary>
public sealed class RateMeter
{
    private readonly TimeSpan _window;
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private readonly object _sync = new();
    private long _windowBytes;

    public RateMeter(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(5);

    public void Add(long bytes)
    {
        if (bytes <= 0) return;
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            _samples.Enqueue((now, bytes));
            _windowBytes += bytes;
            Trim(now);
        }
    }

    /// <summary>Bytes per second over the window, or 0 when nothing recent arrived.</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_sync)
            {
                var now = Stopwatch.GetTimestamp();
                Trim(now);
                if (_samples.Count == 0) return 0;

                var span = (now - _samples.Peek().Ticks) / (double)Stopwatch.Frequency;
                if (span < 0.25) span = 0.25;   // avoid a wild number on the first sample
                return _windowBytes / span;
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _samples.Clear();
            _windowBytes = 0;
        }
    }

    private void Trim(long now)
    {
        var cutoff = now - (long)(_window.TotalSeconds * Stopwatch.Frequency);
        while (_samples.Count > 0 && _samples.Peek().Ticks < cutoff)
            _windowBytes -= _samples.Dequeue().Bytes;
        if (_windowBytes < 0) _windowBytes = 0;
    }
}
