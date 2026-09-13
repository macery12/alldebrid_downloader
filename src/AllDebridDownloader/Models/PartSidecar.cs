using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllDebridDownloader.Models;

/// <summary>One contiguous byte range of a file, and how much of it has landed.</summary>
public sealed class SegmentRecord
{
    [JsonPropertyName("start")] public long Start { get; set; }

    /// <summary>Inclusive end offset.</summary>
    [JsonPropertyName("end")] public long End { get; set; }

    /// <summary>Bytes written so far, counted from <see cref="Start"/>.</summary>
    [JsonPropertyName("written")] public long Written { get; set; }

    [JsonIgnore] public long Length => End - Start + 1;
    [JsonIgnore] public long Remaining => Math.Max(0, Length - Written);
    [JsonIgnore] public bool IsComplete => Written >= Length;

    /// <summary>Where the next byte for this segment belongs in the target file.</summary>
    [JsonIgnore] public long NextOffset => Start + Written;
}

/// <summary>
/// The ".part.json" written beside every ".part" file. This is what makes resume work
/// across an app restart or a crash, so it is flushed often and written atomically.
/// </summary>
public sealed class PartSidecar
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;

    /// <summary>The original alldebrid.com/f/ link. Unlocked URLs expire; this does not.</summary>
    [JsonPropertyName("sourceLink")] public string? SourceLink { get; set; }

    [JsonPropertyName("finalFileName")] public string? FinalFileName { get; set; }
    [JsonPropertyName("torrentName")] public string? TorrentName { get; set; }
    [JsonPropertyName("magnetId")] public long MagnetId { get; set; }

    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }

    /// <summary>Validators used to detect that the remote file changed under us.</summary>
    [JsonPropertyName("eTag")] public string? ETag { get; set; }
    [JsonPropertyName("lastModified")] public string? LastModified { get; set; }

    [JsonPropertyName("supportsRanges")] public bool SupportsRanges { get; set; }

    [JsonPropertyName("segments")] public List<SegmentRecord> Segments { get; set; } = new();

    [JsonPropertyName("updatedUtc")] public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public long DownloadedBytes => Segments.Sum(s => s.Written);

    [JsonIgnore]
    public bool IsComplete => Segments.Count > 0 && Segments.All(s => s.IsComplete);

    /// <summary>
    /// True when this sidecar can be trusted against a fresh probe of the remote file.
    /// A changed size or validator means the file is not the one we started.
    /// </summary>
    public bool MatchesRemote(long? totalSize, string? eTag, string? lastModified)
    {
        if (totalSize is not null && TotalSize > 0 && totalSize != TotalSize) return false;

        // Only reject on a validator that we recorded AND that the server still sends.
        if (!string.IsNullOrEmpty(ETag) && !string.IsNullOrEmpty(eTag)
            && !string.Equals(ETag, eTag, StringComparison.Ordinal)) return false;

        if (!string.IsNullOrEmpty(LastModified) && !string.IsNullOrEmpty(lastModified)
            && !string.Equals(LastModified, lastModified, StringComparison.Ordinal)) return false;

        return true;
    }

    /// <summary>Segments are contiguous, ordered, and cover [0, TotalSize-1] exactly.</summary>
    public bool IsSelfConsistent()
    {
        if (Segments.Count == 0) return false;

        var expectedStart = 0L;
        foreach (var s in Segments)
        {
            if (s.Start != expectedStart) return false;
            if (s.End < s.Start) return false;
            if (s.Written < 0 || s.Written > s.Length) return false;
            expectedStart = s.End + 1;
        }

        return TotalSize <= 0 || expectedStart == TotalSize;
    }
}

/// <summary>Reads and writes <see cref="PartSidecar"/> files atomically.</summary>
public static class SidecarStore
{
    public const string PartExtension = ".part";
    public const string SidecarExtension = ".part.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public static string PartPathFor(string finalPath) => finalPath + PartExtension;
    public static string SidecarPathFor(string finalPath) => finalPath + SidecarExtension;

    /// <summary>Given a .part.json path, the final file path it belongs to.</summary>
    public static string FinalPathFromSidecar(string sidecarPath) =>
        sidecarPath.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase)
            ? sidecarPath[..^SidecarExtension.Length]
            : sidecarPath;

    public static async Task SaveAsync(string finalPath, PartSidecar sidecar, CancellationToken ct = default)
    {
        sidecar.UpdatedUtc = DateTime.UtcNow;
        var path = SidecarPathFor(finalPath);
        var tmp = path + ".tmp";

        var json = JsonSerializer.Serialize(sidecar, Options);
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Null when the sidecar is missing, unreadable, or internally inconsistent.</summary>
    public static PartSidecar? TryLoad(string finalPath)
    {
        var path = SidecarPathFor(finalPath);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            var sidecar = JsonSerializer.Deserialize<PartSidecar>(json, Options);
            if (sidecar is null || sidecar.Version != PartSidecar.CurrentVersion) return null;
            return sidecar.IsSelfConsistent() ? sidecar : null;
        }
        catch
        {
            // Truncated or corrupt: treat it as absent, so the file restarts cleanly.
            return null;
        }
    }

    public static void Delete(string finalPath)
    {
        foreach (var p in new[] { SidecarPathFor(finalPath), SidecarPathFor(finalPath) + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Split a file into <paramref name="count"/> contiguous segments covering every
    /// byte exactly once. Remainder bytes go to the earlier segments.
    /// </summary>
    public static List<SegmentRecord> PlanSegments(long totalSize, int count)
    {
        if (totalSize <= 0) return new List<SegmentRecord> { new() { Start = 0, End = -1 } };

        count = Math.Max(1, count);
        if (count > totalSize) count = (int)totalSize;

        var baseSize = totalSize / count;
        var remainder = (int)(totalSize % count);

        var segments = new List<SegmentRecord>(count);
        var cursor = 0L;
        for (var i = 0; i < count; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            segments.Add(new SegmentRecord { Start = cursor, End = cursor + size - 1, Written = 0 });
            cursor += size;
        }

        return segments;
    }
}
