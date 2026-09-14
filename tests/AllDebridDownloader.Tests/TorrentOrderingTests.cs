using AllDebridDownloader.Services;
using AllDebridDownloader.ViewModels;

namespace AllDebridDownloader.Tests;

/// <summary>
/// What you just submitted has to be where you are already looking: the top of the list,
/// in the order you submitted it, selected.
/// </summary>
public sealed class TorrentOrderingTests : IDisposable
{
    private readonly TestLogger _log = new();
    private readonly TempDir _temp = new();

    public void Dispose()
    {
        _log.Dispose();
        _temp.Dispose();
    }

    private const string Hash1 = "1111111111111111111111111111111111111111";
    private const string Hash2 = "2222222222222222222222222222222222222222";
    private const string Hash3 = "3333333333333333333333333333333333333333";

    /// <summary>A view model whose client is fed a scripted upload response.</summary>
    private MainViewModel MakeViewModel(FakeHandler handler)
    {
        var config = new ConfigService(_log.Logger, _temp.Path);
        config.Load();
        config.Current.DownloadDirectory = _temp.Path;
        config.Current.ApiKey = "TESTKEY0000000000000";

        var vm = new MainViewModel(_log.Logger, config);
        vm.Client.ApiKey = "TESTKEY0000000000000";
        return vm;
    }

    private static string UploadResponse(params (long Id, string Name)[] magnets)
    {
        // Built by concatenation rather than a raw string: the JSON is dense with braces
        // and the escaping obscures what is being asserted.
        var items = string.Join(",", magnets.Select(m =>
            "{\"magnet\":\"magnet:?xt=urn:btih:" + m.Id + "\","
            + "\"hash\":\"h" + m.Id + "\","
            + "\"name\":\"" + m.Name + "\","
            + "\"size\":1000,\"ready\":false,"
            + "\"id\":" + m.Id + "}"));

        return "{\"status\":\"success\",\"data\":{\"magnets\":[" + items + "]}}";
    }

    [Fact]
    public async Task A_newly_added_magnet_lands_at_the_top_and_is_selected()
    {
        var handler = FakeHandler.Json(UploadResponse((999, "Brand.New.Torrent")));
        using var vm = MakeViewModel(handler);
        vm.Client.Dispose();

        using var client = new AllDebridClient(_log.Logger, handler, new ApiThrottle(1000, 100000))
        {
            ApiKey = "TESTKEY0000000000000"
        };

        // Pre-populate the list the way a startup sync would.
        for (var i = 1; i <= 5; i++)
            vm.Torrents.Add(new TorrentViewModel(i) { Name = "Existing " + i });

        var results = await client.UploadMagnetsAsync(["magnet:?xt=urn:btih:" + Hash1]);
        InvokeHandleUploadResults(vm, results);

        Assert.Equal("Brand.New.Torrent", vm.Torrents[0].Name);
        Assert.Equal(999, vm.Torrents[0].Id);
        Assert.Same(vm.Torrents[0], vm.SelectedTorrent);
        Assert.Equal(6, vm.Torrents.Count);
    }

    [Fact]
    public async Task Several_magnets_keep_the_order_they_were_submitted_in()
    {
        var handler = FakeHandler.Json(UploadResponse(
            (101, "First"), (102, "Second"), (103, "Third")));

        using var vm = MakeViewModel(handler);
        vm.Client.Dispose();

        using var client = new AllDebridClient(_log.Logger, handler, new ApiThrottle(1000, 100000))
        {
            ApiKey = "TESTKEY0000000000000"
        };

        vm.Torrents.Add(new TorrentViewModel(1) { Name = "Older" });

        var results = await client.UploadMagnetsAsync(
        [
            "magnet:?xt=urn:btih:" + Hash1,
            "magnet:?xt=urn:btih:" + Hash2,
            "magnet:?xt=urn:btih:" + Hash3
        ]);
        InvokeHandleUploadResults(vm, results);

        // Inserting each at index 0 would have reversed these.
        Assert.Equal("First", vm.Torrents[0].Name);
        Assert.Equal("Second", vm.Torrents[1].Name);
        Assert.Equal("Third", vm.Torrents[2].Name);
        Assert.Equal("Older", vm.Torrents[3].Name);

        Assert.Same(vm.Torrents[0], vm.SelectedTorrent);
    }

    [Fact]
    public async Task Re_adding_a_magnet_already_on_the_account_moves_it_to_the_top()
    {
        var handler = FakeHandler.Json(UploadResponse((42, "Already.Here")));

        using var vm = MakeViewModel(handler);
        vm.Client.Dispose();

        using var client = new AllDebridClient(_log.Logger, handler, new ApiThrottle(1000, 100000))
        {
            ApiKey = "TESTKEY0000000000000"
        };

        // Buried far down the list, as it would be on a busy account.
        for (var i = 1; i <= 5; i++)
            vm.Torrents.Add(new TorrentViewModel(i) { Name = "Other " + i });
        var existing = new TorrentViewModel(42) { Name = "Already.Here" };
        vm.Torrents.Add(existing);
        for (var i = 6; i <= 10; i++)
            vm.Torrents.Add(new TorrentViewModel(i) { Name = "Other " + i });

        var countBefore = vm.Torrents.Count;

        var results = await client.UploadMagnetsAsync(["magnet:?xt=urn:btih:" + Hash1]);
        InvokeHandleUploadResults(vm, results);

        Assert.Same(existing, vm.Torrents[0]);
        Assert.Same(existing, vm.SelectedTorrent);
        Assert.Equal(countBefore, vm.Torrents.Count);   // moved, not duplicated
    }

    [Fact]
    public async Task A_failed_add_also_appears_at_the_top_so_the_error_is_seen()
    {
        const string body = """
        {"status":"success","data":{"magnets":[
          {"magnet":"rubbish","error":{"code":"MAGNET_INVALID_URI","message":"Magnet is not valid"}}
        ]}}
        """;

        var handler = FakeHandler.Json(body);
        using var vm = MakeViewModel(handler);
        vm.Client.Dispose();

        using var client = new AllDebridClient(_log.Logger, handler, new ApiThrottle(1000, 100000))
        {
            ApiKey = "TESTKEY0000000000000"
        };

        for (var i = 1; i <= 4; i++)
            vm.Torrents.Add(new TorrentViewModel(i) { Name = "Existing " + i });

        var results = await client.UploadMagnetsAsync(["magnet:?xt=urn:btih:" + Hash1]);
        InvokeHandleUploadResults(vm, results);

        Assert.Equal(0, vm.Torrents[0].Id);
        Assert.NotNull(vm.Torrents[0].AddError);
        Assert.True(vm.NoticeIsError);
    }

    [Fact]
    public async Task A_mixed_batch_keeps_submitted_order_across_successes_and_failures()
    {
        const string body = """
        {"status":"success","data":{"magnets":[
          {"magnet":"a","hash":"ha","name":"Good.One","size":10,"ready":false,"id":201},
          {"magnet":"bad","error":{"code":"MAGNET_INVALID_URI","message":"Magnet is not valid"}},
          {"magnet":"c","hash":"hc","name":"Good.Two","size":20,"ready":false,"id":202}
        ]}}
        """;

        var handler = FakeHandler.Json(body);
        using var vm = MakeViewModel(handler);
        vm.Client.Dispose();

        using var client = new AllDebridClient(_log.Logger, handler, new ApiThrottle(1000, 100000))
        {
            ApiKey = "TESTKEY0000000000000"
        };

        vm.Torrents.Add(new TorrentViewModel(1) { Name = "Older" });

        var results = await client.UploadMagnetsAsync(
        [
            "magnet:?xt=urn:btih:" + Hash1,
            "magnet:?xt=urn:btih:" + Hash2,
            "magnet:?xt=urn:btih:" + Hash3
        ]);
        InvokeHandleUploadResults(vm, results);

        Assert.Equal("Good.One", vm.Torrents[0].Name);
        Assert.NotNull(vm.Torrents[1].AddError);
        Assert.Equal("Good.Two", vm.Torrents[2].Name);
        Assert.Equal("Older", vm.Torrents[3].Name);
    }

    [Fact]
    public async Task The_view_is_told_to_scroll_the_new_row_into_sight()
    {
        var handler = FakeHandler.Json(UploadResponse((555, "Needs.Scrolling")));

        using var vm = MakeViewModel(handler);
        vm.Client.Dispose();

        using var client = new AllDebridClient(_log.Logger, handler, new ApiThrottle(1000, 100000))
        {
            ApiKey = "TESTKEY0000000000000"
        };

        TorrentViewModel? scrolledTo = null;
        vm.TorrentBroughtToTop += t => scrolledTo = t;

        for (var i = 1; i <= 30; i++)
            vm.Torrents.Add(new TorrentViewModel(i) { Name = "Filler " + i });

        var results = await client.UploadMagnetsAsync(["magnet:?xt=urn:btih:" + Hash1]);
        InvokeHandleUploadResults(vm, results);

        Assert.NotNull(scrolledTo);
        Assert.Equal(555, scrolledTo.Id);
        Assert.Same(vm.Torrents[0], scrolledTo);
    }

    [Fact]
    public void A_magnet_discovered_by_polling_goes_to_the_bottom()
    {
        using var vm = MakeViewModel(FakeHandler.Json("""{"status":"success","data":{}}"""));

        vm.Torrents.Add(new TorrentViewModel(1) { Name = "Mine" });

        // The poller appends rows for magnets added elsewhere, leaving the top for what
        // the user submitted here.
        vm.Torrents.Add(new TorrentViewModel(2) { Name = "From.The.Website" });

        Assert.Equal("Mine", vm.Torrents[0].Name);
        Assert.Equal("From.The.Website", vm.Torrents[^1].Name);
    }

    /// <summary>
    /// HandleUploadResults is private, and deliberately so -- it is an implementation
    /// detail of the add commands. Reflection keeps the test on the real code path
    /// without widening the surface just for testing.
    /// </summary>
    private static void InvokeHandleUploadResults(
        MainViewModel vm, List<Models.UploadedMagnet> results)
    {
        var method = typeof(MainViewModel).GetMethod(
            "HandleUploadResults",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(method);
        method.Invoke(vm, [results, "magnet"]);
    }
}
