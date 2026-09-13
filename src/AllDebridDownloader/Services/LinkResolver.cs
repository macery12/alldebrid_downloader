using System.Collections.Concurrent;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Models;

namespace AllDebridDownloader.Services;

/// <summary>
/// Turns an "alldebrid.com/f/..." link from /v4/magnet/files into a URL that can
/// actually be downloaded.
///
/// The /f/ link is not the final URL -- it has to go through /v4/link/unlock. This
/// mirrors a known-working reference implementation, which unlocked every per-file link
/// before downloading it. Unlocked URLs expire, so callers can force a re-resolve when
/// a cached one starts returning 403/404/410.
/// </summary>
public sealed class LinkResolver
{
    /// <summary>How long an unlocked URL is reused before being refreshed anyway.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(20);

    private readonly AllDebridClient _client;
    private readonly Logger _log;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public LinkResolver(AllDebridClient client, Logger log)
    {
        _client = client;
        _log = log;
    }

    private sealed record CacheEntry(string Url, DateTime ResolvedUtc);

    /// <summary>
    /// Resolve <paramref name="sourceLink"/> to a downloadable URL.
    /// </summary>
    /// <param name="forceRefresh">
    /// Skip the cache and unlock again -- pass true after a 403/404/410 on a cached URL.
    /// </param>
    public async Task<string> ResolveAsync(
        string sourceLink, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceLink))
            throw new AllDebridApiException("LINK_IS_MISSING", null,
                ApiErrorMessages.Friendly("LINK_IS_MISSING", null));

        if (!forceRefresh
            && _cache.TryGetValue(sourceLink, out var cached)
            && DateTime.UtcNow - cached.ResolvedUtc < CacheLifetime)
        {
            return cached.Url;
        }

        var resolved = await UnlockAsync(sourceLink, ct).ConfigureAwait(false);
        _cache[sourceLink] = new CacheEntry(resolved, DateTime.UtcNow);
        return resolved;
    }

    private async Task<string> UnlockAsync(string sourceLink, CancellationToken ct)
    {
        _log.Debug("Link", "Unlocking " + Redact.Url(sourceLink));

        var unlocked = await _client.UnlockAsync(sourceLink, ct).ConfigureAwait(false);

        var direct = unlocked.DirectLink;
        if (!string.IsNullOrWhiteSpace(direct))
        {
            _log.Debug("Link", "Unlocked to " + Redact.Url(direct));
            return direct;
        }

        // No link yet: AllDebrid is generating it. Poll /link/delayed.
        if (unlocked.Delayed is > 0)
        {
            _log.Info("Link", "Link is delayed (id " + unlocked.Delayed + "); waiting for it.");
            return await WaitForDelayedAsync(unlocked.Delayed.Value, ct).ConfigureAwait(false);
        }

        throw new AllDebridApiException("LINK_ERROR", null,
            "AllDebrid unlocked that link but returned no download URL.");
    }

    /// <summary>
    /// Poll /v4/link/delayed until the link exists. The docs ask for 5 s or slower.
    /// </summary>
    private async Task<string> WaitForDelayedAsync(long delayedId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

            var result = await _client.GetDelayedAsync(delayedId, ct).ConfigureAwait(false);

            switch (result.Status)
            {
                case 2 when !string.IsNullOrWhiteSpace(result.Link):
                    _log.Info("Link", "Delayed link is ready.");
                    return result.Link!;

                case 3:
                    throw new AllDebridApiException("LINK_ERROR", null,
                        "AllDebrid could not generate a download link for that file.");

                default:
                    continue;   // status 1: still processing
            }
        }

        throw new AllDebridApiException("LINK_ERROR", null,
            "AllDebrid did not produce a download link within 10 minutes.");
    }

    /// <summary>Drop a cached URL, e.g. when it has started returning 403.</summary>
    public void Invalidate(string sourceLink) => _cache.TryRemove(sourceLink, out _);

    public void Clear() => _cache.Clear();
}
