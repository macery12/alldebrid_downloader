using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Models;

namespace AllDebridDownloader.Services;

/// <summary>
/// Client for the AllDebrid v4 / v4.1 API.
///
/// Conventions, per the current documentation:
///  - auth is the "Authorization: Bearer &lt;apikey&gt;" header; the ?apikey= query
///    parameter is deprecated and is never used here (it would also leak into logs)
///  - no "agent" or "version" parameter -- that requirement was removed 2025-01-15
///  - POST with form-urlencoded bodies for everything except /v4/user and /v4.1/pin/get
///  - array parameters are repeated bracket names: magnets[]=..&amp;magnets[]=..
/// </summary>
public sealed class AllDebridClient : IDisposable
{
    public const string BaseUrl = "https://api.alldebrid.com";

    private readonly HttpClient _http;
    private readonly ApiThrottle _throttle;
    private readonly Logger _log;
    private readonly bool _ownsHttpClient;

    private string? _apiKey;

    public AllDebridClient(Logger log, HttpMessageHandler? handler = null, ApiThrottle? throttle = null)
    {
        _log = log;
        _throttle = throttle ?? new ApiThrottle();
        _ownsHttpClient = handler is null;

        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(15)
            })
            : new HttpClient(handler, disposeHandler: false);

        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(45);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AllDebridDownloader/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>The key used for authenticated calls. Never logged, never in a URL.</summary>
    public string? ApiKey
    {
        get => _apiKey;
        set => _apiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

    /// <summary>Set when a response carried "demo": true, i.e. a staticDemoApikey was used.</summary>
    public bool LastResponseWasDemo { get; private set; }

    // -----------------------------------------------------------------------
    // Core request plumbing
    // -----------------------------------------------------------------------

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        IEnumerable<KeyValuePair<string, string>>? form,
        bool authenticate,
        CancellationToken ct,
        HttpContent? content = null)
    {
        const int maxAttempts = 4;
        Exception? lastTransport = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await _throttle.WaitAsync(ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(method, path);

            if (content is not null)
                request.Content = content;
            else if (form is not null && method != HttpMethod.Get)
                request.Content = new FormUrlEncodedContent(form);

            if (authenticate)
            {
                if (!HasApiKey)
                    throw new AllDebridApiException("AUTH_MISSING_APIKEY", null,
                        ApiErrorMessages.Friendly("AUTH_MISSING_APIKEY", null));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                lastTransport = ex;
                if (attempt == maxAttempts)
                    throw new AllDebridApiException(null, ex.Message,
                        "Could not reach AllDebrid. Check your connection.");
                _log.Warn("Api", "Transport failure on " + path + " (attempt " + attempt + "/" + maxAttempts
                    + "): " + ex.GetType().Name);
                await Task.Delay(BackoffFor(attempt), ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                // Rate limiting: honour Retry-After, then retry.
                if (response.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.ServiceUnavailable)
                {
                    var wait = RetryAfter(response) ?? BackoffFor(attempt);
                    _throttle.ApplyServerBackoff(wait);
                    RateLimited?.Invoke(wait);
                    _log.Warn("Api", "Rate limited on " + path + "; waiting "
                        + wait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s.");

                    if (attempt == maxAttempts)
                        throw new AllDebridApiException("429", null,
                            "AllDebrid is rate limiting this app. Try again in a moment.",
                            (int)response.StatusCode);

                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ParseEnvelope<T>(body, (int)response.StatusCode, path);
            }
        }

        throw new AllDebridApiException(null, lastTransport?.Message,
            "Could not reach AllDebrid. Check your connection.");
    }

    /// <summary>
    /// Turn a raw body into T, or throw a meaningful exception. A body that is not a
    /// valid envelope is a transport-level problem, not an API error.
    /// </summary>
    internal T ParseEnvelope<T>(string body, int httpStatus, string path)
    {
        ApiResponse<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ApiResponse<T>>(body, ApiJson.Options);
        }
        catch (JsonException ex)
        {
            _log.Error("Api", "Response from " + path + " was not valid JSON (http " + httpStatus + ").", ex);
            throw new AllDebridApiException(null, ex.Message,
                "AllDebrid returned a response this app could not read.", httpStatus);
        }

        if (envelope?.Status is null)
        {
            _log.Error("Api", "Response from " + path + " had no status field (http " + httpStatus + ").");
            throw new AllDebridApiException(null, null,
                "AllDebrid returned an unexpected response.", httpStatus);
        }

        LastResponseWasDemo = envelope.Demo;

        if (!envelope.IsSuccess)
        {
            var code = envelope.Error?.Code;
            var apiMessage = envelope.Error?.Message;
            _log.Warn("Api", path + " -> error " + (code ?? "(no code)"));
            throw new AllDebridApiException(code, apiMessage,
                ApiErrorMessages.Friendly(code, apiMessage), httpStatus);
        }

        if (envelope.Data is null)
            throw new AllDebridApiException(null, null,
                "AllDebrid returned success but no data.", httpStatus);

        return envelope.Data;
    }

    private static TimeSpan BackoffFor(int attempt)
    {
        var seconds = Math.Min(30, Math.Pow(2, attempt - 1));
        var jitter = Random.Shared.NextDouble() * 0.4 - 0.2;   // +/-20%
        return TimeSpan.FromSeconds(Math.Max(0.5, seconds * (1 + jitter)));
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is not null) return header.Delta;
        if (header.Date is not null)
        {
            var delta = header.Date.Value - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero) return delta;
        }
        return null;
    }

    /// <summary>Raised when the server rate limited us, with the wait applied.</summary>
    public event Action<TimeSpan>? RateLimited;

    private static KeyValuePair<string, string> Kv(string k, string v) => new(k, v);

    // -----------------------------------------------------------------------
    // Endpoints
    // -----------------------------------------------------------------------

    /// <summary>GET /v4/ping -- public connectivity probe, needs no key.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await SendAsync<JsonElement>(HttpMethod.Get, "/v4/ping", null, false, ct)
                .ConfigureAwait(false);
            return data.TryGetProperty("ping", out var p) && p.GetString() == "pong";
        }
        catch (AllDebridApiException)
        {
            return false;
        }
    }

    /// <summary>GET /v4/user -- also the way an apikey is validated.</summary>
    public async Task<UserInfo> GetUserAsync(CancellationToken ct = default)
    {
        var data = await SendAsync<UserEnvelope>(HttpMethod.Get, "/v4/user", null, true, ct)
            .ConfigureAwait(false);
        if (data.User is null)
            throw new AllDebridApiException(null, null, "AllDebrid returned no account details.");
        return data.User;
    }

    /// <summary>
    /// Validate a key without disturbing the one currently in use.
    /// </summary>
    public async Task<UserInfo> ValidateKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var previous = _apiKey;
        try
        {
            ApiKey = apiKey;
            return await GetUserAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _apiKey = previous;
            throw;
        }
    }

    /// <summary>GET /v4.1/pin/get -- public. Note the 4.1 version.</summary>
    public Task<PinRequest> GetPinAsync(CancellationToken ct = default) =>
        SendAsync<PinRequest>(HttpMethod.Get, "/v4.1/pin/get", null, false, ct);

    /// <summary>POST /v4/pin/check -- public. Poll no faster than every 5 seconds.</summary>
    public Task<PinCheckResult> CheckPinAsync(string pin, string check, CancellationToken ct = default) =>
        SendAsync<PinCheckResult>(HttpMethod.Post, "/v4/pin/check",
            new[] { Kv("pin", pin), Kv("check", check) }, false, ct);

    /// <summary>POST /v4/magnet/upload -- accepts magnet URIs and bare hashes.</summary>
    public async Task<List<UploadedMagnet>> UploadMagnetsAsync(
        IEnumerable<string> magnets, CancellationToken ct = default)
    {
        var form = magnets
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => Kv("magnets[]", m.Trim()))
            .ToList();

        if (form.Count == 0)
            throw new AllDebridApiException("MAGNET_NO_URI", null,
                ApiErrorMessages.Friendly("MAGNET_NO_URI", null));

        _log.Info("Api", "Uploading " + form.Count + " magnet(s).");

        var data = await SendAsync<MagnetUploadEnvelope>(HttpMethod.Post, "/v4/magnet/upload",
            form, true, ct).ConfigureAwait(false);

        return data.Magnets ?? new List<UploadedMagnet>();
    }

    /// <summary>
    /// POST /v4/magnet/upload/file -- multipart. The response key is "files", not
    /// "magnets".
    /// </summary>
    public async Task<List<UploadedMagnet>> UploadTorrentFilesAsync(
        IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        var paths = filePaths.ToList();
        if (paths.Count == 0) return new List<UploadedMagnet>();

        using var multipart = new MultipartFormDataContent();
        var streams = new List<Stream>();

        try
        {
            foreach (var path in paths)
            {
                var stream = File.OpenRead(path);
                streams.Add(stream);
                var part = new StreamContent(stream);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
                multipart.Add(part, "files[]", Path.GetFileName(path));
            }

            _log.Info("Api", "Uploading " + paths.Count + " torrent file(s).");

            var data = await SendAsync<TorrentUploadEnvelope>(HttpMethod.Post,
                "/v4/magnet/upload/file", null, true, ct, multipart).ConfigureAwait(false);

            return data.Files ?? new List<UploadedMagnet>();
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    /// <summary>
    /// POST /v4.1/magnet/status. Pass an id for one magnet (which also returns its
    /// files), a status filter, or nothing for everything.
    /// </summary>
    public async Task<MagnetStatusEnvelope> GetStatusAsync(
        long? id = null, string? statusFilter = null, CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>>();
        if (id is not null) form.Add(Kv("id", id.Value.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(statusFilter)) form.Add(Kv("status", statusFilter));

        return await SendAsync<MagnetStatusEnvelope>(HttpMethod.Post, "/v4.1/magnet/status",
            form, true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// POST /v4.1/magnet/status in live mode. The first call for a session returns
    /// fullsync=true with everything; later calls return only changed fields.
    /// </summary>
    public async Task<MagnetStatusEnvelope> GetStatusLiveAsync(
        long session, long counter, CancellationToken ct = default)
    {
        var form = new[]
        {
            Kv("session", session.ToString(CultureInfo.InvariantCulture)),
            Kv("counter", counter.ToString(CultureInfo.InvariantCulture))
        };

        return await SendAsync<MagnetStatusEnvelope>(HttpMethod.Post, "/v4.1/magnet/status",
            form, true, ct).ConfigureAwait(false);
    }

    /// <summary>POST /v4/magnet/files -- the only source of download links.</summary>
    public async Task<List<MagnetFilesEntry>> GetFilesAsync(
        IEnumerable<long> magnetIds, CancellationToken ct = default)
    {
        var ids = magnetIds.Distinct().ToList();
        if (ids.Count == 0) return new List<MagnetFilesEntry>();

        var form = ids.Select(i => Kv("id[]", i.ToString(CultureInfo.InvariantCulture))).ToList();

        var data = await SendAsync<MagnetFilesEnvelope>(HttpMethod.Post, "/v4/magnet/files",
            form, true, ct).ConfigureAwait(false);

        var entries = data.Magnets ?? new List<MagnetFilesEntry>();
        _log.Info("Api", "Retrieved file trees for " + entries.Count + " magnet(s).");
        return entries;
    }

    /// <summary>POST /v4/magnet/delete.</summary>
    public async Task<string> DeleteMagnetAsync(long id, CancellationToken ct = default)
    {
        var data = await SendAsync<MessageEnvelope>(HttpMethod.Post, "/v4/magnet/delete",
            new[] { Kv("id", id.ToString(CultureInfo.InvariantCulture)) }, true, ct)
            .ConfigureAwait(false);
        _log.Info("Api", "Deleted magnet " + id + ".");
        return data.Message ?? "Deleted.";
    }

    /// <summary>POST /v4/magnet/restart -- only valid for magnets in an error state.</summary>
    public async Task<string> RestartMagnetAsync(long id, CancellationToken ct = default)
    {
        var data = await SendAsync<MessageEnvelope>(HttpMethod.Post, "/v4/magnet/restart",
            new[] { Kv("id", id.ToString(CultureInfo.InvariantCulture)) }, true, ct)
            .ConfigureAwait(false);
        _log.Info("Api", "Restarted magnet " + id + ".");
        return data.Message ?? "Restarted.";
    }

    /// <summary>
    /// POST /v4/link/unlock. Required before downloading: the alldebrid.com/f/ links
    /// from magnet/files are not the final URL.
    /// </summary>
    public Task<UnlockResult> UnlockAsync(string link, CancellationToken ct = default) =>
        SendAsync<UnlockResult>(HttpMethod.Post, "/v4/link/unlock",
            new[] { Kv("link", link) }, true, ct);

    /// <summary>POST /v4/link/delayed -- poll no faster than every 5 seconds.</summary>
    public Task<DelayedResult> GetDelayedAsync(long delayedId, CancellationToken ct = default) =>
        SendAsync<DelayedResult>(HttpMethod.Post, "/v4/link/delayed",
            new[] { Kv("id", delayedId.ToString(CultureInfo.InvariantCulture)) }, true, ct);

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
        _throttle.Dispose();
    }
}
