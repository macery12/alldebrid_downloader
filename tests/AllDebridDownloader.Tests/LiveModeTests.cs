using AllDebridDownloader.Models;
using AllDebridDownloader.Services;

namespace AllDebridDownloader.Tests;

/// <summary>
/// Live-mode status parsing, driven by payloads captured from the real API.
///
/// The documentation shows "magnets" as an array in every case. It is not: a delta that
/// carries a single changed magnet sends a bare object instead, which made the whole poll
/// fail to parse and knocked the app back to full-status polling.
/// </summary>
public sealed class LiveModeTests : IDisposable
{
    private readonly TestLogger _log = new();

    public void Dispose() => _log.Dispose();

    /// <summary>Captured verbatim from POST /v4.1/magnet/status with session+counter=0.</summary>
    private const string FullSyncBody = """
    {
        "status": "success",
        "demo": true,
        "data": {
            "fullsync": true,
            "counter": 1,
            "magnets": [
                {
                    "id": 123456,
                    "filename": "ubuntu-16.04.2-live-server-amd64.iso",
                    "hash": "123456789cef825718eda30637230585e3330599",
                    "size": 587400285,
                    "status": "Downloading",
                    "statusCode": 1,
                    "downloaded": 255400192,
                    "uploaded": 0,
                    "seeders": 7,
                    "downloadSpeed": 18874368,
                    "uploadSpeed": 0,
                    "uploadDate": 1557133868,
                    "completionDate": 0
                },
                {
                    "id": 56789,
                    "filename": "ubuntu-20.04.2-live-server-amd64.iso",
                    "hash": "987654321cef825718eda30637230585e3330599",
                    "size": 256400192,
                    "status": "Ready",
                    "statusCode": 4,
                    "uploadDate": 1657133868,
                    "completionDate": 1657133968,
                    "nbLinks": 1
                },
                {
                    "id": 99999,
                    "filename": "ubuntu-12.04.2-live-server-amd64.iso",
                    "hash": "999999999cef825718eda30637230585e3330599",
                    "size": 256400192,
                    "status": "Error : Download took more than 72h",
                    "statusCode": 10,
                    "uploadDate": 1657133868,
                    "completionDate": 1657133968
                }
            ]
        }
    }
    """;

    /// <summary>Captured verbatim from the same endpoint with counter=1. Note the object.</summary>
    private const string DeltaBody = """
    {
        "status": "success",
        "demo": true,
        "data": {
            "counter": 2,
            "magnets": {
                "id": 123456,
                "downloaded": 358454178,
                "seeders": 9,
                "downloadSpeed": 27874410
            }
        }
    }
    """;

    private AllDebridClient ClientFor(FakeHandler handler) =>
        new(_log.Logger, handler, new ApiThrottle(1000, 100000)) { ApiKey = "TESTKEY0000000000000" };

    [Fact]
    public async Task A_fullsync_parses_every_magnet()
    {
        using var client = ClientFor(FakeHandler.Json(FullSyncBody));
        var envelope = await client.GetStatusLiveAsync(12345, 0);

        Assert.True(envelope.FullSync);
        Assert.Equal(1, envelope.Counter);
        Assert.Equal(3, envelope.Magnets!.Count);

        var downloading = envelope.Magnets[0];
        Assert.Equal(123456, downloading.Id);
        Assert.Equal(MagnetStatusKind.Processing, downloading.Kind);
        Assert.Equal(255400192, downloading.Downloaded);
        Assert.Equal("123456789cef825718eda30637230585e3330599", downloading.Hash);

        var ready = envelope.Magnets[1];
        Assert.True(ready.IsReady);
        Assert.Null(ready.Downloaded);      // omitted once finished

        var failed = envelope.Magnets[2];
        Assert.Equal(MagnetStatusKind.Error, failed.Kind);
        Assert.Equal("Download took more than 72 h", failed.StatusText);

        // "nbLinks" is not modelled; an unknown field must not break the parse.
        Assert.True(client.LastResponseWasDemo);
    }

    [Fact]
    public async Task A_delta_sent_as_a_bare_object_parses_as_one_magnet()
    {
        // This is the exact payload that used to throw
        // "The JSON value could not be converted to List<MagnetStatus>".
        using var client = ClientFor(FakeHandler.Json(DeltaBody));
        var envelope = await client.GetStatusLiveAsync(12345, 1);

        Assert.Equal(2, envelope.Counter);
        Assert.Null(envelope.FullSync);

        Assert.NotNull(envelope.Magnets);
        Assert.Single(envelope.Magnets);

        var delta = envelope.Magnets[0];
        Assert.Equal(123456, delta.Id);
        Assert.Equal(358454178, delta.Downloaded);
        Assert.Equal(9, delta.Seeders);
        Assert.Equal(27874410, delta.DownloadSpeed);

        // Only the changed fields are sent; everything else must stay null so applying
        // the delta does not wipe the state we already hold.
        Assert.Null(delta.Filename);
        Assert.Null(delta.Size);
        Assert.Null(delta.StatusCode);
    }

    [Fact]
    public void Applying_a_delta_updates_only_what_changed()
    {
        var held = new MagnetStatus
        {
            Id = 123456,
            Filename = "ubuntu-16.04.2-live-server-amd64.iso",
            Size = 587400285,
            Status = "Downloading",
            StatusCode = 1,
            Downloaded = 255400192,
            Seeders = 7,
            DownloadSpeed = 18874368
        };

        held.ApplyDelta(new MagnetStatus
        {
            Id = 123456,
            Downloaded = 358454178,
            Seeders = 9,
            DownloadSpeed = 27874410
        });

        Assert.Equal(358454178, held.Downloaded);
        Assert.Equal(9, held.Seeders);
        Assert.Equal(27874410, held.DownloadSpeed);

        // Untouched by the delta.
        Assert.Equal("ubuntu-16.04.2-live-server-amd64.iso", held.Filename);
        Assert.Equal(587400285, held.Size);
        Assert.Equal(1, held.StatusCode);
    }

    [Fact]
    public async Task An_empty_delta_means_nothing_changed()
    {
        const string body = """
        {"status":"success","data":{"counter":3,"magnets":[]}}
        """;

        using var client = ClientFor(FakeHandler.Json(body));
        var envelope = await client.GetStatusLiveAsync(12345, 2);

        Assert.Equal(3, envelope.Counter);
        Assert.Empty(envelope.Magnets!);
    }

    [Fact]
    public async Task A_single_object_is_tolerated_on_the_other_magnet_endpoints_too()
    {
        // Same shape-shifting has been seen on the upload and files endpoints by an
        // older working client, so they get the same tolerance.
        using var upload = ClientFor(FakeHandler.Json("""
        {"status":"success","data":{"magnets":
          {"magnet":"m","hash":"h","name":"Only.One","size":10,"ready":true,"id":7}}}
        """));
        var uploaded = await upload.UploadMagnetsAsync(["magnet:?xt=urn:btih:" + new string('a', 40)]);
        Assert.Single(uploaded);
        Assert.Equal(7, uploaded[0].Id);

        using var files = ClientFor(FakeHandler.Json("""
        {"status":"success","data":{"magnets":
          {"id":"7","files":[{"n":"a.iso","s":5,"l":"https://alldebrid.com/f/a"}]}}}
        """));
        var entries = await files.GetFilesAsync([7]);
        Assert.Single(entries);
        Assert.Equal(7, entries[0].Id);
        Assert.Single(entries[0].Files!);
    }

    [Fact]
    public async Task A_torrent_upload_of_one_file_is_tolerated_as_an_object()
    {
        using var temp = new TempDir();
        var torrent = temp.File("x.torrent");
        await File.WriteAllTextAsync(torrent, "d8:announcee");

        using var client = ClientFor(FakeHandler.Json("""
        {"status":"success","data":{"files":
          {"file":"x.torrent","name":"Only.One","size":10,"hash":"h","ready":false,"id":9}}}
        """));

        var results = await client.UploadTorrentFilesAsync([torrent]);

        Assert.Single(results);
        Assert.Equal(9, results[0].Id);
    }

    [Fact]
    public async Task The_poller_survives_a_fullsync_followed_by_an_object_delta()
    {
        // End to end through MagnetPoller: this is the sequence that knocked live mode
        // out and forced the fallback to full-status polling.
        var handler = FakeHandler.Sequence(
            () => FakeHandler.JsonResponse(FullSyncBody),
            () => FakeHandler.JsonResponse(DeltaBody));

        using var client = ClientFor(handler);
        using var temp = new TempDir();

        var config = new ConfigService(_log.Logger, temp.Path);
        config.Load();

        using var poller = new MagnetPoller(client, _log.Logger, config);

        IReadOnlyList<MagnetStatus>? snapshot = null;
        poller.StatusUpdated += s => snapshot = s;

        await InvokePollOnceAsync(poller);
        Assert.Equal(3, snapshot!.Count);
        Assert.True(poller.IsLiveMode);

        await InvokePollOnceAsync(poller);

        // Still three magnets, with the delta merged onto the one it named.
        Assert.Equal(3, snapshot.Count);
        Assert.True(poller.IsLiveMode);   // never fell back

        var updated = poller.Get(123456);
        Assert.NotNull(updated);
        Assert.Equal(358454178, updated.Downloaded);
        Assert.Equal(9, updated.Seeders);

        // And the untouched fields survived the merge.
        Assert.Equal("ubuntu-16.04.2-live-server-amd64.iso", updated.Filename);
        Assert.Equal(587400285, updated.Size);
    }

    private static Task InvokePollOnceAsync(MagnetPoller poller)
    {
        var method = typeof(MagnetPoller).GetMethod(
            "PollOnceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(method);
        return (Task)method.Invoke(poller, [CancellationToken.None])!;
    }
}
