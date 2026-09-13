using System.Net;
using AllDebridDownloader.Models;
using AllDebridDownloader.Services;

namespace AllDebridDownloader.Tests;

public sealed class ApiParsingTests : IDisposable
{
    private readonly TestLogger _log = new();

    public void Dispose() => _log.Dispose();

    private AllDebridClient ClientFor(FakeHandler handler)
    {
        // A throttle with generous limits keeps the tests fast.
        var client = new AllDebridClient(_log.Logger, handler, new ApiThrottle(1000, 100000))
        {
            ApiKey = "TESTKEY0000000000000"
        };
        return client;
    }

    // -----------------------------------------------------------------------
    // Envelope
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Success_envelope_is_unwrapped()
    {
        const string body = """
        {"status":"success","data":{"user":{"username":"bob","isPremium":true,
        "premiumUntil":1899888000,"isTrial":false}}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var user = await client.GetUserAsync();

        Assert.Equal("bob", user.Username);
        Assert.True(user.IsPremium);
        Assert.Contains("Premium until", user.AccountSummary);
    }

    [Fact]
    public async Task Error_envelope_becomes_a_friendly_exception()
    {
        const string body = """
        {"status":"error","error":{"code":"AUTH_BAD_APIKEY","message":"The auth apikey is invalid"}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var ex = await Assert.ThrowsAsync<AllDebridApiException>(() => client.GetUserAsync());

        Assert.Equal("AUTH_BAD_APIKEY", ex.Code);
        Assert.Equal("Your AllDebrid API key is not valid.", ex.Message);
        Assert.True(ex.IsAuthFailure);
        Assert.Contains("AUTH_BAD_APIKEY", ex.TechnicalDetails);
    }

    [Fact]
    public async Task Unknown_error_code_falls_back_to_the_api_message()
    {
        const string body = """
        {"status":"error","error":{"code":"SOME_NEW_CODE","message":"Something novel happened"}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var ex = await Assert.ThrowsAsync<AllDebridApiException>(() => client.GetUserAsync());

        Assert.Equal("Something novel happened", ex.Message);
    }

    [Fact]
    public async Task Non_json_body_is_reported_as_unreadable()
    {
        using var client = ClientFor(new FakeHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>maintenance</html>")
            }));

        var ex = await Assert.ThrowsAsync<AllDebridApiException>(() => client.GetUserAsync());
        Assert.Contains("could not read", ex.Message);
    }

    [Fact]
    public async Task Json_without_a_status_field_is_rejected()
    {
        using var client = ClientFor(FakeHandler.Json("""{"data":{"user":{"username":"x"}}}"""));

        var ex = await Assert.ThrowsAsync<AllDebridApiException>(() => client.GetUserAsync());
        Assert.Contains("unexpected response", ex.Message);
    }

    [Fact]
    public async Task Demo_flag_is_read_even_when_sent_as_a_string()
    {
        const string body = """
        {"status":"success","demo":"true","data":{"user":{"username":"demoUserPremium","isPremium":true}}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        await client.GetUserAsync();

        Assert.True(client.LastResponseWasDemo);
    }

    [Fact]
    public async Task Auth_header_is_bearer_and_the_key_never_reaches_the_url()
    {
        var handler = FakeHandler.Json("""{"status":"success","data":{"user":{"username":"b"}}}""");
        using var client = ClientFor(handler);
        await client.GetUserAsync();

        var request = handler.Requests[0];
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("TESTKEY0000000000000", request.Headers.Authorization.Parameter);
        Assert.DoesNotContain("TESTKEY", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task No_agent_parameter_is_sent()
    {
        // The agent requirement was removed 2025-01-15; old clients still send it.
        var handler = FakeHandler.Json("""{"status":"success","data":{"magnets":[]}}""");
        using var client = ClientFor(handler);
        await client.UploadMagnetsAsync(["magnet:?xt=urn:btih:" + new string('a', 40)]);

        Assert.DoesNotContain("agent", handler.Requests[0].RequestUri!.ToString());
        Assert.DoesNotContain("agent=", handler.Bodies[0]);
    }

    // -----------------------------------------------------------------------
    // Uploads
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Magnet_upload_handles_mixed_success_and_per_item_errors()
    {
        const string body = """
        {"status":"success","data":{"magnets":[
          {"magnet":"magnet:?xt=urn:btih:aaa","hash":"aaa","name":"Ubuntu","size":875773970,
           "ready":true,"id":123456},
          {"magnet":"bad","error":{"code":"MAGNET_INVALID_URI","message":"Magnet is not valid"}},
          {"magnet":"magnet:?xt=urn:btih:ccc","hash":"ccc","name":"Other","size":9024340026,
           "ready":false,"id":234567}
        ]}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var results = await client.UploadMagnetsAsync(["a", "b", "c"]);

        Assert.Equal(3, results.Count);

        Assert.False(results[0].Failed);
        Assert.Equal(123456, results[0].Id);
        Assert.Equal(875773970, results[0].Size);
        Assert.True(results[0].Ready);

        Assert.True(results[1].Failed);
        Assert.Equal("MAGNET_INVALID_URI", results[1].Error!.Code);

        Assert.False(results[2].Failed);
        Assert.Equal(9024340026, results[2].Size);   // over int.MaxValue: must be long
    }

    [Fact]
    public async Task Magnet_upload_sends_repeated_bracket_parameters()
    {
        var handler = FakeHandler.Json("""{"status":"success","data":{"magnets":[]}}""");
        using var client = ClientFor(handler);

        await client.UploadMagnetsAsync(["one", "two"]);

        var body = handler.Bodies[0];
        Assert.Equal(2, body.Split("magnets%5B%5D=").Length - 1);
    }

    [Fact]
    public async Task Torrent_upload_reads_the_files_key_not_magnets()
    {
        // This endpoint returns "files", unlike every other magnet endpoint.
        const string body = """
        {"status":"success","data":{"files":[
          {"file":"ubuntu.torrent","name":"Ubuntu 18.04.2","size":1954210119,
           "hash":"842783e3","ready":false,"id":123456},
          {"file":"not.a.torrent.zip",
           "error":{"code":"MAGNET_INVALID_FILE","message":"File is not a valid torrent"}}
        ]}}
        """;

        using var temp = new TempDir();
        var torrent = temp.File("x.torrent");
        await File.WriteAllTextAsync(torrent, "d8:announce...e");

        using var client = ClientFor(FakeHandler.Json(body));
        var results = await client.UploadTorrentFilesAsync([torrent]);

        Assert.Equal(2, results.Count);
        Assert.Equal(123456, results[0].Id);
        Assert.Equal("Ubuntu 18.04.2", results[0].Name);
        Assert.Equal("ubuntu.torrent", results[0].File);
        Assert.True(results[1].Failed);
    }

    // -----------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Status_parses_a_downloading_magnet()
    {
        const string body = """
        {"status":"success","data":{"magnets":[
          {"id":123456,"filename":"ubuntu.iso","size":587400285,"status":"Downloading",
           "statusCode":1,"downloaded":255400192,"uploaded":0,"seeders":7,
           "downloadSpeed":18874368,"uploadSpeed":0,"uploadDate":1557133868,"completionDate":0}
        ]}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var envelope = await client.GetStatusAsync();
        var magnet = envelope.Magnets![0];

        Assert.Equal(123456, magnet.Id);
        Assert.Equal(MagnetStatusKind.Processing, magnet.Kind);
        Assert.Equal("Downloading", magnet.StatusText);
        Assert.Equal(255400192, magnet.Downloaded);
        Assert.Equal(7, magnet.Seeders);
        Assert.False(magnet.IsReady);
    }

    [Fact]
    public async Task Status_tolerates_a_finished_magnet_with_progress_fields_omitted()
    {
        // AllDebrid drops downloaded/seeders/downloadSpeed once a magnet is Ready.
        const string body = """
        {"status":"success","data":{"magnets":[
          {"id":56789,"filename":"done.iso","size":256400192,"status":"Ready","statusCode":4,
           "uploadDate":1657133868,"completionDate":1657133968}
        ]}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var magnet = (await client.GetStatusAsync()).Magnets![0];

        Assert.True(magnet.IsReady);
        Assert.Equal(MagnetStatusKind.Ready, magnet.Kind);
        Assert.Null(magnet.Downloaded);
        Assert.Null(magnet.Seeders);
        Assert.Null(magnet.DownloadSpeed);
    }

    [Theory]
    [InlineData(0, MagnetStatusKind.Processing, "In queue")]
    [InlineData(1, MagnetStatusKind.Processing, "Downloading")]
    [InlineData(2, MagnetStatusKind.Processing, "Compressing / moving")]
    [InlineData(3, MagnetStatusKind.Processing, "Uploading")]
    [InlineData(4, MagnetStatusKind.Ready, "Ready")]
    [InlineData(5, MagnetStatusKind.Error, "Upload failed")]
    [InlineData(6, MagnetStatusKind.Error, "Internal error on unpacking")]
    [InlineData(7, MagnetStatusKind.Error, "Not downloaded in 20 min")]
    [InlineData(8, MagnetStatusKind.Error, "File too big")]
    [InlineData(9, MagnetStatusKind.Error, "Internal error")]
    [InlineData(10, MagnetStatusKind.Error, "Download took more than 72 h")]
    [InlineData(11, MagnetStatusKind.Error, "Deleted on the hoster website")]
    [InlineData(12, MagnetStatusKind.Error, "Processing failed")]
    [InlineData(13, MagnetStatusKind.Error, "Processing failed")]
    [InlineData(14, MagnetStatusKind.Error, "Error while contacting tracker")]
    [InlineData(15, MagnetStatusKind.Error, "File not available - no peer")]
    public void Every_documented_status_code_maps(int code, MagnetStatusKind kind, string text)
    {
        Assert.Equal(kind, StatusCodes.Classify(code));
        Assert.Equal(text, StatusCodes.Describe(code));
    }

    [Fact]
    public void Unmapped_status_code_is_described_not_thrown()
    {
        // A future API addition must not crash the app.
        Assert.Equal(MagnetStatusKind.Unknown, StatusCodes.Classify(99));
        Assert.Equal("Unknown status (code 99)", StatusCodes.Describe(99));
        Assert.Equal("Unknown status", StatusCodes.Describe(null));
        Assert.Equal("Weird (code 42)", StatusCodes.Describe(42, "Weird"));
    }

    [Fact]
    public async Task Unmapped_status_code_survives_deserialization()
    {
        const string body = """
        {"status":"success","data":{"magnets":[{"id":1,"statusCode":99,"status":"Brand new"}]}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var magnet = (await client.GetStatusAsync()).Magnets![0];

        Assert.Equal(MagnetStatusKind.Unknown, magnet.Kind);
        Assert.Contains("99", magnet.StatusText);
    }

    // -----------------------------------------------------------------------
    // The string-vs-number id trap
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Magnet_files_id_arrives_as_a_json_string()
    {
        // /v4/magnet/files returns "id":"123" -- a string.
        const string body = """
        {"status":"success","data":{"magnets":[
          {"id":"123","files":[{"n":"a.iso","s":5665497088,"l":"https://alldebrid.com/f/xxx"}]}
        ]}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var entries = await client.GetFilesAsync([123]);

        Assert.Equal(123, entries[0].Id);
        Assert.Equal(5665497088, entries[0].Files![0].Size);
    }

    [Fact]
    public async Task Magnet_status_id_arrives_as_a_json_number()
    {
        // /v4.1/magnet/status returns "id":123456 -- a number. Same model type.
        const string body = """{"status":"success","data":{"magnets":[{"id":123456}]}}""";

        using var client = ClientFor(FakeHandler.Json(body));
        var envelope = await client.GetStatusAsync();

        Assert.Equal(123456, envelope.Magnets![0].Id);
    }

    [Fact]
    public async Task Magnet_files_reports_a_per_item_error_alongside_successes()
    {
        const string body = """
        {"status":"success","data":{"magnets":[
          {"id":"123","files":[{"n":"a.iso","s":1,"l":"https://alldebrid.com/f/a"}]},
          {"id":"456","error":{"code":"MAGNET_INVALID_ID",
            "message":"This magnet ID does not exists or is invalid"}}
        ]}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var entries = await client.GetFilesAsync([123, 456]);

        Assert.Null(entries[0].Error);
        Assert.Equal("MAGNET_INVALID_ID", entries[1].Error!.Code);
        Assert.Null(entries[1].Files);
    }

    // -----------------------------------------------------------------------
    // PIN
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Pin_get_is_parsed()
    {
        const string body = """
        {"status":"success","data":{"pin":"ABCD","check":"664c3ca2","expires_in":600,
        "user_url":"https://alldebrid.com/pin/?pin=ABCD","base_url":"https://alldebrid.com/pin/"}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var pin = await client.GetPinAsync();

        Assert.Equal("ABCD", pin.Pin);
        Assert.Equal("664c3ca2", pin.Check);
        Assert.Equal(600, pin.ExpiresIn);
        Assert.Equal("https://alldebrid.com/pin/?pin=ABCD", pin.UserUrl);
    }

    [Fact]
    public async Task Pin_check_pending_then_activated()
    {
        var handler = FakeHandler.Sequence(
            () => FakeHandler.JsonResponse(
                """{"status":"success","data":{"activated":false,"expires_in":581}}"""),
            () => FakeHandler.JsonResponse(
                """{"status":"success","data":{"apikey":"abcdefABCDEF12345678","activated":true,"expires_in":570}}"""));

        using var client = ClientFor(handler);

        var pending = await client.CheckPinAsync("ABCD", "chk");
        Assert.False(pending.Activated);
        Assert.Null(pending.ApiKey);

        var done = await client.CheckPinAsync("ABCD", "chk");
        Assert.True(done.Activated);
        Assert.Equal("abcdefABCDEF12345678", done.ApiKey);
    }

    [Theory]
    [InlineData("PIN_EXPIRED", "That PIN expired. Get a new one.")]
    [InlineData("PIN_INVALID", "That PIN is no longer valid. Starting over.")]
    public async Task Pin_errors_map_to_friendly_text(string code, string expected)
    {
        var body = $"{{\"status\":\"error\",\"error\":{{\"code\":\"{code}\",\"message\":\"x\"}}}}";

        using var client = ClientFor(FakeHandler.Json(body));
        var ex = await Assert.ThrowsAsync<AllDebridApiException>(
            () => client.CheckPinAsync("ABCD", "chk"));

        Assert.Equal(code, ex.Code);
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public async Task Pin_endpoints_are_called_without_auth()
    {
        var handler = FakeHandler.Json(
            """{"status":"success","data":{"pin":"ABCD","check":"c","expires_in":600}}""");

        using var client = ClientFor(handler);
        await client.GetPinAsync();

        Assert.Null(handler.Requests[0].Headers.Authorization);
        Assert.Contains("/v4.1/pin/get", handler.Requests[0].RequestUri!.ToString());
    }

    // -----------------------------------------------------------------------
    // Link unlocking
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Unlock_returns_the_direct_link()
    {
        const string body = """
        {"status":"success","data":{"link":"https://cdn.debrid.it/dl/abc/file.iso",
        "filename":"file.iso","filesize":1234,"host":"alldebrid"}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var result = await client.UnlockAsync("https://alldebrid.com/f/xxx");

        Assert.Equal("https://cdn.debrid.it/dl/abc/file.iso", result.DirectLink);
    }

    [Fact]
    public async Task Unlock_accepts_download_or_url_as_alternates()
    {
        // The working reference implementation tried link, then download, then url.
        using var a = ClientFor(FakeHandler.Json(
            """{"status":"success","data":{"download":"https://x/d"}}"""));
        Assert.Equal("https://x/d", (await a.UnlockAsync("l")).DirectLink);

        using var b = ClientFor(FakeHandler.Json(
            """{"status":"success","data":{"url":"https://x/u"}}"""));
        Assert.Equal("https://x/u", (await b.UnlockAsync("l")).DirectLink);
    }

    [Fact]
    public async Task Unlock_with_no_link_but_a_delayed_id_is_recognised()
    {
        const string body = """{"status":"success","data":{"delayed":2457,"filename":"f.iso"}}""";

        using var client = ClientFor(FakeHandler.Json(body));
        var result = await client.UnlockAsync("l");

        Assert.Null(result.DirectLink);
        Assert.Equal(2457, result.Delayed);
    }

    [Fact]
    public async Task Delayed_status_values_are_parsed()
    {
        using var processing = ClientFor(FakeHandler.Json(
            """{"status":"success","data":{"status":1,"time_left":45}}"""));
        var p = await processing.GetDelayedAsync(1);
        Assert.Equal(1, p.Status);
        Assert.Equal(45, p.TimeLeft);

        using var ready = ClientFor(FakeHandler.Json(
            """{"status":"success","data":{"status":2,"time_left":0,"link":"https://x/f.iso"}}"""));
        var r = await ready.GetDelayedAsync(1);
        Assert.Equal(2, r.Status);
        Assert.Equal("https://x/f.iso", r.Link);
    }

    [Fact]
    public async Task Resolver_unlocks_then_caches_the_result()
    {
        var handler = FakeHandler.Json(
            """{"status":"success","data":{"link":"https://cdn/x/file.iso"}}""");

        using var client = ClientFor(handler);
        var resolver = new LinkResolver(client, _log.Logger);

        var first = await resolver.ResolveAsync("https://alldebrid.com/f/abc");
        var second = await resolver.ResolveAsync("https://alldebrid.com/f/abc");

        Assert.Equal("https://cdn/x/file.iso", first);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.CallCount);   // second call came from cache
    }

    [Fact]
    public async Task Resolver_force_refresh_bypasses_the_cache()
    {
        var handler = FakeHandler.Sequence(
            () => FakeHandler.JsonResponse("""{"status":"success","data":{"link":"https://cdn/one"}}"""),
            () => FakeHandler.JsonResponse("""{"status":"success","data":{"link":"https://cdn/two"}}"""));

        using var client = ClientFor(handler);
        var resolver = new LinkResolver(client, _log.Logger);

        Assert.Equal("https://cdn/one", await resolver.ResolveAsync("l"));
        Assert.Equal("https://cdn/two", await resolver.ResolveAsync("l", forceRefresh: true));
        Assert.Equal(2, handler.CallCount);
    }

    // -----------------------------------------------------------------------
    // Rate limiting
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Retries_after_a_503_then_succeeds()
    {
        var handler = FakeHandler.Sequence(
            () =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                r.Headers.Add("Retry-After", "0");
                return r;
            },
            () => FakeHandler.JsonResponse(
                """{"status":"success","data":{"user":{"username":"bob"}}}"""));

        using var client = ClientFor(handler);
        var user = await client.GetUserAsync();

        Assert.Equal("bob", user.Username);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Rate_limit_event_reports_the_wait()
    {
        var handler = FakeHandler.Sequence(
            () =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.Add("Retry-After", "0");
                return r;
            },
            () => FakeHandler.JsonResponse(
                """{"status":"success","data":{"user":{"username":"bob"}}}"""));

        using var client = ClientFor(handler);

        TimeSpan? reported = null;
        client.RateLimited += w => reported = w;

        await client.GetUserAsync();
        Assert.NotNull(reported);
    }

    [Fact]
    public async Task Delete_and_restart_return_their_message()
    {
        using var del = ClientFor(FakeHandler.Json(
            """{"status":"success","data":{"message":"Magnet was successfully deleted"}}"""));
        Assert.Equal("Magnet was successfully deleted", await del.DeleteMagnetAsync(1));

        using var res = ClientFor(FakeHandler.Json(
            """{"status":"success","data":{"message":"Magnet was successfully restarted"}}"""));
        Assert.Equal("Magnet was successfully restarted", await res.RestartMagnetAsync(1));
    }

    [Fact]
    public async Task Restart_on_a_processing_magnet_surfaces_the_code()
    {
        using var client = ClientFor(FakeHandler.Json(
            """{"status":"error","error":{"code":"MAGNET_PROCESSING","message":"Magnet is processing or completed"}}"""));

        var ex = await Assert.ThrowsAsync<AllDebridApiException>(() => client.RestartMagnetAsync(1));
        Assert.Equal("MAGNET_PROCESSING", ex.Code);
    }

    [Fact]
    public async Task Missing_api_key_fails_before_any_request()
    {
        var handler = FakeHandler.Json("""{"status":"success","data":{}}""");
        using var client = new AllDebridClient(_log.Logger, handler, new ApiThrottle(1000, 100000));

        var ex = await Assert.ThrowsAsync<AllDebridApiException>(() => client.GetUserAsync());
        Assert.Equal("AUTH_MISSING_APIKEY", ex.Code);
        Assert.Equal(0, handler.CallCount);
    }
}
