namespace AllDebridDownloader.Services;

/// <summary>
/// Keeps API calls inside AllDebrid's documented limits (12 requests/second and
/// 600 requests/minute) rather than relying on the server to push back with 429s.
/// Sliding windows, and only one call is admitted at a time.
/// </summary>
public sealed class ApiThrottle : IDisposable
{
    private readonly int _perSecond;
    private readonly int _perMinute;
    private readonly Queue<DateTime> _recent = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Set when the server tells us to back off; every caller waits for it.
    private DateTime _backoffUntil = DateTime.MinValue;

    public ApiThrottle(int perSecond = 10, int perMinute = 550)
    {
        // Deliberately under the documented 12/s and 600/min so a burst of retries
        // cannot tip us over.
        _perSecond = perSecond;
        _perMinute = perMinute;
    }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var now = DateTime.UtcNow;

                if (_backoffUntil > now)
                {
                    var wait = _backoffUntil - now;
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(1))
                    _recent.Dequeue();

                var inLastSecond = 0;
                foreach (var t in _recent)
                    if (now - t <= TimeSpan.FromSeconds(1)) inLastSecond++;

                if (inLastSecond < _perSecond && _recent.Count < _perMinute)
                {
                    _recent.Enqueue(now);
                    return;
                }

                // Sleep just long enough for the tightest window to free a slot.
                var delay = inLastSecond >= _perSecond
                    ? TimeSpan.FromMilliseconds(120)
                    : TimeSpan.FromSeconds(1);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called after a 429/503 so every subsequent call waits this out.</summary>
    public void ApplyServerBackoff(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        if (until > _backoffUntil) _backoffUntil = until;
    }

    public TimeSpan RemainingBackoff
    {
        get
        {
            var remaining = _backoffUntil - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public void Dispose() => _gate.Dispose();
}
