using System.Text;

namespace AllDebridDownloader.Helpers;

/// <summary>
/// All filesystem-name handling for names that came from the AllDebrid API.
/// Remote names are untrusted: a torrent must never be able to write outside the
/// folder chosen for it.
/// </summary>
public static class PathHelper
{
    private const string Placeholder = "unnamed";

    /// <summary>Windows device names, reserved with or without an extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly char[] ExtraInvalid = { ':', '*', '?', '"', '<', '>', '|', '/', '\\' };

    private static readonly char[] Separators = { '/', '\\' };

    /// <summary>
    /// Make one path segment safe to use as a single file or directory name.
    /// Collapses runs of illegal characters to a single underscore, neutralises
    /// reserved device names, and never returns an empty string.
    /// </summary>
    public static string SanitizeSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return Placeholder;

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        foreach (var c in ExtraInvalid) invalid.Add(c);

        var sb = new StringBuilder(segment.Length);
        var lastWasReplacement = false;
        foreach (var ch in segment)
        {
            if (invalid.Contains(ch) || char.IsControl(ch))
            {
                if (!lastWasReplacement)
                {
                    sb.Append('_');
                    lastWasReplacement = true;
                }
                continue;
            }
            sb.Append(ch);
            lastWasReplacement = false;
        }

        var result = sb.ToString().Trim();

        // "." and ".." are directory references, not names.
        if (result == "." || result == "..") return Placeholder;

        // Windows silently strips trailing dots and spaces; do it explicitly so the
        // name we record is the name that lands on disk.
        result = result.TrimEnd('.', ' ');
        if (result.Length == 0) return Placeholder;

        // CON, nul.txt, LPT1.mkv -> suffix with an underscore, keeping the extension.
        var stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedNames.Contains(stem))
        {
            var ext = Path.GetExtension(result);
            result = stem + "_" + ext;
        }

        // A name that survived only as replacement characters tells the user nothing.
        if (result.All(c => c == '_')) return Placeholder;

        return result.Length == 0 ? Placeholder : result;
    }

    /// <summary>
    /// Sanitize a relative path made of several segments. Absolute paths, drive
    /// letters, UNC prefixes and traversal segments are all stripped rather than
    /// honoured — the caller's root always wins.
    /// </summary>
    public static string SanitizeRelativePath(IEnumerable<string> segments)
    {
        var safe = new List<string>();
        foreach (var raw in segments)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // A single "segment" may itself contain separators if the API ever returns
            // a path inside one node name. Split it and vet every part.
            foreach (var part in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed == "." || trimmed == "..") continue;             // traversal
                if (trimmed.Length == 2 && trimmed[1] == ':') continue;      // "C:" drive spec
                safe.Add(SanitizeSegment(trimmed));
            }
        }
        return safe.Count == 0 ? Placeholder : string.Join(Path.DirectorySeparatorChar, safe);
    }

    /// <summary>
    /// Combine <paramref name="root"/> with an untrusted relative path and prove the
    /// result stays inside the root.
    /// </summary>
    /// <returns>The full, safe path.</returns>
    /// <exception cref="UnsafePathException">
    /// The combined path resolved outside <paramref name="root"/>.
    /// </exception>
    public static string CombineSafe(string root, IEnumerable<string> untrustedSegments)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root must be a rooted path.", nameof(root));

        var relative = SanitizeRelativePath(untrustedSegments);
        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relative));

        if (!IsInside(rootFull, combined))
            throw new UnsafePathException(relative, combined, rootFull);

        return combined;
    }

    /// <summary>True when <paramref name="candidate"/> is the root itself or below it.</summary>
    public static bool IsInside(string root, string candidate)
    {
        var a = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var b = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        return b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shorten a path that would exceed the filesystem limits, trimming the filename
    /// stem but keeping its extension.
    /// </summary>
    /// <returns>The shortened path, or null when nothing needed changing.</returns>
    /// <exception cref="PathTooLongException">
    /// The directory part alone exceeds the limit, so no filename can make it fit.
    /// </exception>
    public static string? ShortenIfTooLong(string fullPath, int maxTotalLength = 32000)
    {
        // With longPathAware in the manifest the practical ceiling is ~32,767, but a
        // single component is still capped at 255 on NTFS.
        var dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var name = Path.GetFileName(fullPath);

        var changed = false;

        if (name.Length > 255)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 40) ext = ext.Substring(0, 40);
            var stem = Path.GetFileNameWithoutExtension(name);
            var keep = Math.Max(1, 255 - ext.Length);
            name = stem.Substring(0, Math.Min(stem.Length, keep)) + ext;
            changed = true;
        }

        var candidate = Path.Combine(dir, name);
        if (candidate.Length > maxTotalLength)
        {
            var ext = Path.GetExtension(name);
            var room = maxTotalLength - dir.Length - 1 - ext.Length;
            if (room < 1)
            {
                // No filename can rescue this; the caller must refuse the file rather
                // than proceed with a path Windows will reject later.
                throw new PathTooLongException(
                    "The destination folder path is too long (" + dir.Length
                    + " characters) to hold this file. Choose a shorter download folder.");
            }
            var stem = Path.GetFileNameWithoutExtension(name);
            name = stem.Substring(0, Math.Min(stem.Length, room)) + ext;
            candidate = Path.Combine(dir, name);
            changed = true;
        }

        return changed ? candidate : null;
    }

    /// <summary>
    /// Pick the first free "name (2).ext", "name (3).ext" for a path that already exists.
    /// </summary>
    public static string NextAvailableName(string fullPath)
    {
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) return fullPath;

        var dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);

        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        // Pathological: return something unique rather than looping forever.
        return Path.Combine(dir, stem + " (" + Guid.NewGuid().ToString("N") + ")" + ext);
    }
}

/// <summary>Thrown when an untrusted name would have escaped the download folder.</summary>
public sealed class UnsafePathException : Exception
{
    public UnsafePathException(string relativePath, string resolvedPath, string root)
        : base("Refused a path that resolved outside the download folder: '" + relativePath + "'")
    {
        RelativePath = relativePath;
        ResolvedPath = resolvedPath;
        Root = root;
    }

    public string RelativePath { get; }
    public string ResolvedPath { get; }
    public string Root { get; }
}
