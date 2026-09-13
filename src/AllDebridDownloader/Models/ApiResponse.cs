using System.Text.Json;
using System.Text.Json.Serialization;
using AllDebridDownloader.Helpers;

namespace AllDebridDownloader.Models;

/// <summary>
/// The AllDebrid envelope. Every response carries "status", either "success" or
/// "error"; success responses carry "data", errors carry "error".
/// </summary>
public sealed class ApiResponse<T>
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("error")]
    public ApiError? Error { get; set; }

    /// <summary>Demo apikeys make the API set this; handy in tests.</summary>
    [JsonPropertyName("demo")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool Demo { get; set; }

    [JsonIgnore]
    public bool IsSuccess => string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);
}

public sealed class ApiError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Thrown when AllDebrid answered with status="error", or when the response was not a
/// usable envelope at all. <see cref="Code"/> is the API's code where there was one.
/// </summary>
public sealed class AllDebridApiException : Exception
{
    public AllDebridApiException(string? code, string? apiMessage, string friendlyMessage, int? httpStatus = null)
        : base(friendlyMessage)
    {
        Code = code;
        ApiMessage = apiMessage;
        HttpStatus = httpStatus;
    }

    public string? Code { get; }
    public string? ApiMessage { get; }
    public int? HttpStatus { get; }

    /// <summary>Text for the collapsed "Technical details" expander. Carries no secrets.</summary>
    public string TechnicalDetails
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Code)) parts.Add("code=" + Code);
            if (HttpStatus is not null) parts.Add("http=" + HttpStatus);
            if (!string.IsNullOrEmpty(ApiMessage)) parts.Add("api=\"" + ApiMessage + "\"");
            return parts.Count == 0 ? "(no further detail)" : string.Join("  ", parts);
        }
    }

    /// <summary>True for the codes that mean the saved apikey is no longer usable.</summary>
    public bool IsAuthFailure => Code is "AUTH_BAD_APIKEY" or "AUTH_MISSING_APIKEY"
        or "AUTH_BLOCKED" or "AUTH_USER_BANNED";
}

/// <summary>
/// Maps API error codes to something a person can act on. Unknown codes fall back to
/// the API's own message, so a new code degrades gracefully instead of showing a bare
/// identifier.
/// </summary>
public static class ApiErrorMessages
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GENERIC"] = "AllDebrid reported an unspecified error.",
        ["404"] = "That AllDebrid endpoint does not exist. The app may need updating.",
        ["MAINTENANCE"] = "AllDebrid is under maintenance. Try again shortly.",

        ["AUTH_MISSING_APIKEY"] = "No AllDebrid API key was sent.",
        ["AUTH_BAD_APIKEY"] = "Your AllDebrid API key is not valid.",
        ["AUTH_BLOCKED"] = "This API key is geo-blocked or IP-blocked. If you are on a VPN, see https://alldebrid.com/vpn",
        ["AUTH_USER_BANNED"] = "This AllDebrid account is banned.",

        ["NO_SERVER"] = "AllDebrid blocked this request as coming from a server. If you are on a VPN, see https://alldebrid.com/vpn",

        ["PIN_ALREADY_AUTHED"] = "This account already has a valid API key. Use the API key option instead.",
        ["PIN_EXPIRED"] = "That PIN expired. Get a new one.",
        ["PIN_INVALID"] = "That PIN is no longer valid. Starting over.",

        ["MAGNET_NO_URI"] = "No magnet was sent.",
        ["MAGNET_INVALID_URI"] = "That magnet link is not valid.",
        ["MAGNET_INVALID_ID"] = "AllDebrid no longer has that torrent.",
        ["MAGNET_INVALID_FILE"] = "That file is not a valid .torrent.",
        ["MAGNET_FILE_UPLOAD_FAILED"] = "Uploading the .torrent file failed.",
        ["MAGNET_MUST_BE_PREMIUM"] = "This needs an active AllDebrid premium subscription.",
        ["MAGNET_NO_SERVER"] = "AllDebrid blocked this request as coming from a server. If you are on a VPN, see https://alldebrid.com/vpn",
        ["MAGNET_TOO_MANY_ACTIVE"] = "AllDebrid allows 30 active magnets at once. Delete a finished one and try again.",
        ["MAGNET_TOO_MANY"] = "You have reached AllDebrid's limit of 1000 magnets.",
        ["MAGNET_TOO_LARGE"] = "That torrent is larger than AllDebrid's 1 TB limit.",
        ["MAGNET_PROCESSING"] = "That torrent is still processing, or has already finished.",
        ["MAGNET_UPLOAD_FAILED"] = "AllDebrid could not upload that torrent.",
        ["MAGNET_INTERNAL_ERROR"] = "AllDebrid hit an internal error with that torrent.",
        ["MAGNET_CANT_BOOTSTRAP"] = "No peers were found within 20 minutes.",
        ["MAGNET_TOOK_TOO_LONG"] = "The download took more than 72 hours and was stopped.",
        ["MAGNET_LINKS_REMOVED"] = "The files were removed from AllDebrid's storage.",
        ["MAGNET_PROCESSING_FAILED"] = "AllDebrid could not process that torrent. It may be malformed.",

        ["LINK_IS_MISSING"] = "No link was sent.",
        ["BAD_LINK"] = "That link is not valid.",
        ["LINK_HOST_NOT_SUPPORTED"] = "AllDebrid does not support that host.",
        ["LINK_DOWN"] = "That file is no longer available.",
        ["LINK_PASS_PROTECTED"] = "That link is password protected.",
        ["LINK_HOST_UNAVAILABLE"] = "That host is under maintenance or unavailable.",
        ["LINK_TOO_MANY_DOWNLOADS"] = "Too many downloads running on that host at once. Lower the simultaneous file count.",
        ["LINK_HOST_FULL"] = "All of AllDebrid's servers for that host are full. Try again shortly.",
        ["LINK_HOST_LIMIT_REACHED"] = "You have reached your download limit for that host.",
        ["LINK_ERROR"] = "AllDebrid could not unlock that link.",
        ["LINK_TEMPORARY_UNAVAILABLE"] = "That file is temporarily unavailable.",
        ["LINK_NOT_SUPPORTED"] = "That link type is not supported for this host.",
        ["DELAYED_INVALID_ID"] = "AllDebrid lost track of that pending link. Retry the file.",

        ["MUST_BE_PREMIUM"] = "This needs an active AllDebrid premium subscription.",
        ["FREE_TRIAL_LIMIT_REACHED"] = "The free trial limit has been reached (7 days / 25 GB)."
    };

    public static string Friendly(string? code, string? apiMessage)
    {
        if (!string.IsNullOrEmpty(code) && Map.TryGetValue(code, out var friendly)) return friendly;
        if (!string.IsNullOrWhiteSpace(apiMessage)) return apiMessage;
        if (!string.IsNullOrEmpty(code)) return "AllDebrid reported: " + code;
        return "AllDebrid reported an error with no detail.";
    }
}

/// <summary>Shared serializer options, including the tolerant id converters.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        o.Converters.Add(new FlexibleLongConverter());
        o.Converters.Add(new FlexibleNullableLongConverter());
        return o;
    }
}
