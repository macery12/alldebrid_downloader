namespace AllDebridDownloader.Helpers;

/// <summary>
/// Every URL and secret that goes near the log passes through here first.
/// AllDebrid links identify a file and, once unlocked, grant access to it, so they are
/// treated as secrets too.
/// </summary>
public static class Redact
{
    /// <summary>
    /// Reduce a URL to scheme+host plus a truncated tail, e.g.
    /// "https://alldebrid.com/f/1a2b…". Never returns the full path or query.
    /// </summary>
    public static string Url(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(none)";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "(unparseable url)";

        var path = uri.AbsolutePath.TrimEnd('/');
        var lastSegment = path.Length == 0
            ? string.Empty
            : path[(path.LastIndexOf('/') + 1)..];

        var hint = lastSegment.Length <= 4 ? lastSegment : lastSegment[..4] + "\u2026";
        var prefix = path.LastIndexOf('/') > 0 ? path[..(path.LastIndexOf('/') + 1)] : "/";

        return $"{uri.Scheme}://{uri.Host}{prefix}{hint}";
    }

    /// <summary>Reduce an apikey or token to a length-only description.</summary>
    public static string Secret(string? secret) =>
        string.IsNullOrEmpty(secret) ? "(none)" : $"({secret.Length} chars, redacted)";
}
