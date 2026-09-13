using System.Text.Json;
using System.Text.Json.Serialization;
using AllDebridDownloader.Helpers;

namespace AllDebridDownloader.Models;

// ---------------------------------------------------------------------------
// GET /v4/user
// ---------------------------------------------------------------------------

public sealed class UserEnvelope
{
    [JsonPropertyName("user")]
    public UserInfo? User { get; set; }
}

public sealed class UserInfo
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("isPremium")] public bool IsPremium { get; set; }
    [JsonPropertyName("isSubscribed")] public bool IsSubscribed { get; set; }
    [JsonPropertyName("isTrial")] public bool IsTrial { get; set; }
    [JsonPropertyName("premiumUntil")] public long PremiumUntil { get; set; }
    [JsonPropertyName("lang")] public string? Lang { get; set; }
    [JsonPropertyName("fidelityPoints")] public long FidelityPoints { get; set; }

    [JsonIgnore]
    public DateTimeOffset? PremiumUntilDate => PremiumUntil > 0
        ? DateTimeOffset.FromUnixTimeSeconds(PremiumUntil)
        : null;

    /// <summary>Short account description for the status bar.</summary>
    [JsonIgnore]
    public string AccountSummary
    {
        get
        {
            var tier = IsPremium ? (IsTrial ? "Trial" : "Premium") : "Free";
            var until = PremiumUntilDate;
            return until is not null && IsPremium
                ? tier + " until " + until.Value.ToLocalTime().ToString("yyyy-MM-dd")
                : tier;
        }
    }
}

// ---------------------------------------------------------------------------
// PIN auth
// ---------------------------------------------------------------------------

public sealed class PinRequest
{
    [JsonPropertyName("pin")] public string? Pin { get; set; }
    [JsonPropertyName("check")] public string? Check { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("user_url")] public string? UserUrl { get; set; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
}

public sealed class PinCheckResult
{
    [JsonPropertyName("activated")] public bool Activated { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("apikey")] public string? ApiKey { get; set; }
}

// ---------------------------------------------------------------------------
// POST /v4/magnet/upload  and  POST /v4/magnet/upload/file
// ---------------------------------------------------------------------------

/// <summary>
/// One item from a magnet or torrent upload. The two endpoints differ: /magnet/upload
/// returns them under "magnets" with a "magnet" field, /magnet/upload/file returns them
/// under "files" with a "file" field. Both share id/hash/name/size/ready.
/// </summary>
public sealed class UploadedMagnet
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long Id { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("hash")] public string? Hash { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("ready")] public bool Ready { get; set; }

    /// <summary>Echo of the magnet URI sent (magnet/upload only).</summary>
    [JsonPropertyName("magnet")] public string? Magnet { get; set; }

    /// <summary>Echo of the uploaded filename (magnet/upload/file only).</summary>
    [JsonPropertyName("file")] public string? File { get; set; }

    /// <summary>Per-item failure. The envelope can still be status=success.</summary>
    [JsonPropertyName("error")] public ApiError? Error { get; set; }

    [JsonIgnore]
    public bool Failed => Error is not null || Id == 0;

    /// <summary>What the user sent, for an error row that has no id or name.</summary>
    [JsonIgnore]
    public string SourceLabel => File ?? Magnet ?? Hash ?? "(unknown)";
}

public sealed class MagnetUploadEnvelope
{
    [JsonPropertyName("magnets")] public List<UploadedMagnet>? Magnets { get; set; }
}

public sealed class TorrentUploadEnvelope
{
    /// <summary>Note the key: this endpoint returns "files", not "magnets".</summary>
    [JsonPropertyName("files")] public List<UploadedMagnet>? Files { get; set; }
}

// ---------------------------------------------------------------------------
// POST /v4.1/magnet/status
// ---------------------------------------------------------------------------

public enum MagnetStatusKind { Processing, Ready, Error, Unknown }

/// <summary>
/// A magnet's status. Progress fields are nullable on purpose: AllDebrid omits
/// downloaded / seeders / downloadSpeed entirely once a magnet is Ready, and live mode
/// sends only the fields that changed.
/// </summary>
public sealed class MagnetStatus
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long Id { get; set; }

    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("statusCode")] public int? StatusCode { get; set; }
    [JsonPropertyName("downloaded")] public long? Downloaded { get; set; }
    [JsonPropertyName("uploaded")] public long? Uploaded { get; set; }
    [JsonPropertyName("seeders")] public int? Seeders { get; set; }
    [JsonPropertyName("downloadSpeed")] public long? DownloadSpeed { get; set; }
    [JsonPropertyName("uploadSpeed")] public long? UploadSpeed { get; set; }
    [JsonPropertyName("uploadDate")] public long? UploadDate { get; set; }
    [JsonPropertyName("completionDate")] public long? CompletionDate { get; set; }

    /// <summary>
    /// Present when a single magnet was requested by id. Multi-magnet file retrieval
    /// still needs /v4/magnet/files.
    /// </summary>
    [JsonPropertyName("files")] public List<MagnetFileNode>? Files { get; set; }

    [JsonIgnore]
    public MagnetStatusKind Kind => StatusCodes.Classify(StatusCode);

    [JsonIgnore]
    public bool IsReady => StatusCode == 4;

    [JsonIgnore]
    public string StatusText => StatusCodes.Describe(StatusCode, Status);

    /// <summary>Copy the non-null fields of a live-mode delta onto this object.</summary>
    public void ApplyDelta(MagnetStatus delta)
    {
        if (delta.Filename is not null) Filename = delta.Filename;
        if (delta.Size is not null) Size = delta.Size;
        if (delta.Status is not null) Status = delta.Status;
        if (delta.StatusCode is not null) StatusCode = delta.StatusCode;
        if (delta.Downloaded is not null) Downloaded = delta.Downloaded;
        if (delta.Uploaded is not null) Uploaded = delta.Uploaded;
        if (delta.Seeders is not null) Seeders = delta.Seeders;
        if (delta.DownloadSpeed is not null) DownloadSpeed = delta.DownloadSpeed;
        if (delta.UploadSpeed is not null) UploadSpeed = delta.UploadSpeed;
        if (delta.UploadDate is not null) UploadDate = delta.UploadDate;
        if (delta.CompletionDate is not null) CompletionDate = delta.CompletionDate;
        if (delta.Files is not null) Files = delta.Files;
    }
}

/// <summary>The documented statusCode table, plus a safe answer for anything new.</summary>
public static class StatusCodes
{
    private static readonly Dictionary<int, string> Descriptions = new()
    {
        [0] = "In queue",
        [1] = "Downloading",
        [2] = "Compressing / moving",
        [3] = "Uploading",
        [4] = "Ready",
        [5] = "Upload failed",
        [6] = "Internal error on unpacking",
        [7] = "Not downloaded in 20 min",
        [8] = "File too big",
        [9] = "Internal error",
        [10] = "Download took more than 72 h",
        [11] = "Deleted on the hoster website",
        [12] = "Processing failed",
        [13] = "Processing failed",
        [14] = "Error while contacting tracker",
        [15] = "File not available - no peer"
    };

    public static MagnetStatusKind Classify(int? code) => code switch
    {
        >= 0 and <= 3 => MagnetStatusKind.Processing,
        4 => MagnetStatusKind.Ready,
        >= 5 and <= 15 => MagnetStatusKind.Error,
        _ => MagnetStatusKind.Unknown
    };

    /// <summary>
    /// Never throws on an unmapped code: an unrecognised value is displayed as such so
    /// a future API addition cannot crash the app.
    /// </summary>
    public static string Describe(int? code, string? apiText = null)
    {
        if (code is null)
            return string.IsNullOrWhiteSpace(apiText) ? "Unknown status" : apiText;

        if (Descriptions.TryGetValue(code.Value, out var text)) return text;

        return string.IsNullOrWhiteSpace(apiText)
            ? "Unknown status (code " + code.Value + ")"
            : apiText + " (code " + code.Value + ")";
    }
}

public sealed class MagnetStatusEnvelope
{
    [JsonPropertyName("magnets")] public List<MagnetStatus>? Magnets { get; set; }
    [JsonPropertyName("counter")] public long? Counter { get; set; }
    [JsonPropertyName("fullsync")] public bool? FullSync { get; set; }
}

// ---------------------------------------------------------------------------
// POST /v4/magnet/files
// ---------------------------------------------------------------------------

/// <summary>
/// A node in a magnet's file tree. A folder has "e" (entries); a file has "s" (size)
/// and "l" (link). Distinguish on the presence of "e", not on s/l being null.
/// </summary>
public sealed class MagnetFileNode
{
    [JsonPropertyName("n")] public string? Name { get; set; }
    [JsonPropertyName("s")] public long? Size { get; set; }
    [JsonPropertyName("l")] public string? Link { get; set; }
    [JsonPropertyName("e")] public List<MagnetFileNode>? Entries { get; set; }

    [JsonIgnore]
    public bool IsFolder => Entries is not null;
}

/// <summary>
/// One magnet's files. Note that this endpoint returns "id" as a JSON *string* while
/// magnet/status returns it as a number, which is why Id uses the flexible converter.
/// </summary>
public sealed class MagnetFilesEntry
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long Id { get; set; }

    [JsonPropertyName("files")] public List<MagnetFileNode>? Files { get; set; }
    [JsonPropertyName("error")] public ApiError? Error { get; set; }
}

public sealed class MagnetFilesEnvelope
{
    [JsonPropertyName("magnets")] public List<MagnetFilesEntry>? Magnets { get; set; }
}

// ---------------------------------------------------------------------------
// POST /v4/link/unlock  and  POST /v4/link/delayed
// ---------------------------------------------------------------------------

public sealed class UnlockResult
{
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("filesize")] public long? FileSize { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("hostDomain")] public string? HostDomain { get; set; }

    /// <summary>Set when the link needs generating; poll /link/delayed with it.</summary>
    [JsonPropertyName("delayed")] public long? Delayed { get; set; }

    // The reference implementation accepted "download" and "url" as alternates for the
    // unlocked URL, so keep tolerating them.
    [JsonPropertyName("download")] public string? Download { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonIgnore]
    public string? DirectLink =>
        FirstNonEmpty(Link, Download, Url);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }
}

public sealed class DelayedResult
{
    /// <summary>1 = still processing, 2 = link available, 3 = failed.</summary>
    [JsonPropertyName("status")] public int Status { get; set; }

    [JsonPropertyName("time_left")] public int TimeLeft { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }
}

public sealed class MessageEnvelope
{
    [JsonPropertyName("message")] public string? Message { get; set; }

    /// <summary>Multi-id restart returns an array instead of a single message.</summary>
    [JsonPropertyName("magnets")] public JsonElement? Magnets { get; set; }
}
