using AllDebridDownloader.Models;
using AllDebridDownloader.Services;
using AllDebridDownloader.ViewModels;

namespace AllDebridDownloader.Tests;

/// <summary>
/// The Home wizard: the newest ready torrents as cards, everything else as chips, and a
/// Pick -> Files -> Started loop that always finds its way back to Pick.
/// </summary>
public sealed class HomeWizardTests : IDisposable
{
    private readonly TestLogger _log = new();
    private readonly TempDir _temp = new();
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    public void Dispose()
    {
        _log.Dispose();
        _temp.Dispose();
    }

    private (MainViewModel Vm, ConfigService Config) MakeViewModel()
    {
        var config = new ConfigService(_log.Logger, _temp.Path);
        config.Load();
        config.Current.DownloadDirectory = _temp.Path;
        return (new MainViewModel(_log.Logger, config), config);
    }

    /// <summary>FilesRequested stops selection from starting a real fetch mid-assertion.</summary>
    private static TorrentViewModel Ready(long id, double hoursAgo) => new(id)
    {
        Name = "Ready " + id,
        Kind = MagnetStatusKind.Ready,
        StatusText = "Ready",
        FilesRequested = true,
        AddedAt = Now.AddHours(-hoursAgo)
    };

    private static TorrentViewModel Processing(long id, double hoursAgo) => new(id)
    {
        Name = "Processing " + id,
        Kind = MagnetStatusKind.Processing,
        StatusText = "Downloading",
        AddedAt = Now.AddHours(-hoursAgo)
    };

    private TransferItem Item(string name, long size) => new()
    {
        SourceLink = "https://alldebrid.com/f/" + name,
        FinalPath = Path.Combine(_temp.Path, "T", name),
        FileName = name,
        RelativePath = Path.Combine("T", name),
        MagnetId = 1,
        TorrentName = "T",
        ExpectedSize = size
    };

    // ---- cards and chips -------------------------------------------------

    [Fact]
    public void Ready_cards_are_the_newest_ready_torrents_first()
    {
        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        vm.Torrents.Add(Ready(1, hoursAgo: 30));
        vm.Torrents.Add(Ready(2, hoursAgo: 1));
        vm.Torrents.Add(Processing(3, hoursAgo: 0.1));
        vm.Torrents.Add(Ready(4, hoursAgo: 5));

        Assert.Equal([2L, 4L, 1L], vm.RecentReady.Select(t => t.Id));
        Assert.True(vm.HasRecentReady);
    }

    [Fact]
    public void Ready_cards_are_capped_so_home_stays_short()
    {
        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        for (var i = 1; i <= 9; i++) vm.Torrents.Add(Ready(i, hoursAgo: i));

        Assert.Equal(6, vm.RecentReady.Count);
        Assert.Equal(1, vm.RecentReady[0].Id);
        Assert.Equal(6, vm.RecentReady[^1].Id);
        Assert.Equal("All torrents (9)", vm.AllTorrentsLinkText);
    }

    [Fact]
    public void A_torrent_added_this_session_outranks_newer_uploads()
    {
        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        vm.Torrents.Add(Ready(1, hoursAgo: 2));

        // Uploaded to the account days ago, but the user just re-added it.
        var readded = Ready(2, hoursAgo: 72);
        readded.TouchedAt = DateTimeOffset.Now;
        vm.Torrents.Add(readded);

        Assert.Same(readded, vm.RecentReady[0]);
    }

    [Fact]
    public void Torrents_without_any_date_keep_list_order()
    {
        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        vm.Torrents.Add(new TorrentViewModel(1) { Name = "A", Kind = MagnetStatusKind.Ready, FilesRequested = true });
        vm.Torrents.Add(new TorrentViewModel(2) { Name = "B", Kind = MagnetStatusKind.Ready, FilesRequested = true });

        Assert.Equal([1L, 2L], vm.RecentReady.Select(t => t.Id));
    }

    [Fact]
    public void Torrents_that_are_not_ready_become_chips_with_an_overflow_count()
    {
        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        for (var i = 1; i <= 6; i++) vm.Torrents.Add(Processing(i, hoursAgo: i));

        Assert.False(vm.HasRecentReady);
        Assert.Equal(4, vm.PendingTorrents.Count);
        Assert.Equal(1, vm.PendingTorrents[0].Id);
        Assert.True(vm.HasPendingMore);
        Assert.Equal("+2 more", vm.PendingMoreText);
    }

    [Fact]
    public void Removing_a_torrent_updates_home()
    {
        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        var gone = Ready(1, hoursAgo: 1);
        vm.Torrents.Add(gone);
        vm.Torrents.Add(Ready(2, hoursAgo: 2));

        vm.Torrents.Remove(gone);

        Assert.Equal([2L], vm.RecentReady.Select(t => t.Id));
    }

    // ---- the wizard loop -------------------------------------------------

    [Fact]
    public void Home_starts_on_the_pick_step()
    {
        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        Assert.True(vm.ShowPickStep);
        Assert.False(vm.ShowFilesStep);
        Assert.False(vm.ShowStartedStep);
    }

    [Fact]
    public void Opening_a_torrent_shows_its_files_and_home_goes_back_to_the_start()
    {
        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        var torrent = Ready(1, hoursAgo: 1);
        vm.Torrents.Add(torrent);

        vm.OpenTorrent(torrent);

        Assert.True(vm.ShowFilesStep);
        Assert.False(vm.ShowPickStep);
        Assert.Same(torrent, vm.SelectedTorrent);

        vm.GoHome();

        Assert.True(vm.ShowPickStep);
        Assert.Null(vm.SelectedTorrent);
    }

    [Fact]
    public void The_started_step_follows_its_batch_until_home_is_pressed()
    {
        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        var torrent = Ready(1, hoursAgo: 1);
        vm.Torrents.Add(torrent);
        vm.OpenTorrent(torrent);

        var batch = new DownloadBatchViewModel("T", Path.Combine(_temp.Path, "T"), [Item("a.mkv", 100)]);
        vm.ShowStarted(batch);

        Assert.True(vm.ShowStartedStep);
        Assert.Same(batch, vm.CurrentBatch);

        vm.GoHome();

        Assert.True(vm.ShowPickStep);
        Assert.Null(vm.CurrentBatch);
    }

    [Fact]
    public void Classic_view_is_remembered_between_runs()
    {
        var (vm, config) = MakeViewModel();

        vm.IsClassicView = true;

        Assert.True(config.Current.ClassicHome);
        Assert.False(vm.IsWizardView);
        Assert.False(vm.ShowPickStep);
        vm.Dispose();

        using var reopened = new MainViewModel(_log.Logger, config);
        Assert.True(reopened.IsClassicView);
    }

    [Fact]
    public void Opening_a_torrent_from_classic_switches_to_the_wizard()
    {
        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        var torrent = Ready(1, hoursAgo: 1);
        vm.Torrents.Add(torrent);
        vm.IsClassicView = true;

        vm.OpenTorrent(torrent);

        Assert.False(vm.IsClassicView);
        Assert.True(vm.ShowFilesStep);
    }

    [Fact]
    public async Task Adding_one_magnet_that_is_already_ready_goes_straight_to_its_files()
    {
        const string body = """
        {"status":"success","data":{"magnets":[
          {"magnet":"magnet:?xt=urn:btih:1111111111111111111111111111111111111111",
           "hash":"h777","name":"Cached.Torrent","size":1000,"ready":true,"id":777}
        ]}}
        """;

        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        var results = await UploadAsync(body);
        InvokeHandleUploadResults(vm, results);

        Assert.True(vm.ShowFilesStep);
        Assert.Equal(777, vm.SelectedTorrent?.Id);
    }

    [Fact]
    public async Task Adding_a_magnet_that_is_still_processing_lands_on_home()
    {
        const string body = """
        {"status":"success","data":{"magnets":[
          {"magnet":"magnet:?xt=urn:btih:2222222222222222222222222222222222222222",
           "hash":"h888","name":"Slow.Torrent","size":1000,"ready":false,"id":888}
        ]}}
        """;

        var (vm, _) = MakeViewModel();
        using var _vm = vm;

        // Even from the files step of something else.
        var other = Ready(1, hoursAgo: 1);
        vm.Torrents.Add(other);
        vm.OpenTorrent(other);

        var results = await UploadAsync(body);
        InvokeHandleUploadResults(vm, results);

        Assert.True(vm.ShowPickStep);
        Assert.Contains(vm.PendingTorrents, t => t.Id == 888);
    }

    // ---- the batch on the started step ------------------------------------

    [Fact]
    public void A_batch_reports_its_own_progress()
    {
        var a = Item("a.mkv", 1000);
        var b = Item("b.mkv", 1000);
        using var batch = new DownloadBatchViewModel("T", "folder", [a, b]);

        a.State = TransferState.Downloading;
        a.DownloadedBytes = 250;
        b.State = TransferState.Completed;
        b.DownloadedBytes = 1000;

        Assert.Equal(62.5, batch.Percent);
        Assert.Equal("62%", batch.PercentText);
        Assert.Equal(1, batch.FinishedCount);
        Assert.False(batch.IsFinished);
        Assert.StartsWith("Downloading 2 files from T", batch.TitleText);

        a.DownloadedBytes = 1000;
        a.State = TransferState.Completed;

        Assert.True(batch.IsFinished);
        Assert.Equal("Downloaded 2 files from T", batch.TitleText);
        Assert.Equal("Done", batch.EtaText);
    }

    [Fact]
    public void A_batch_with_a_failure_says_so()
    {
        var a = Item("a.mkv", 1000);
        using var batch = new DownloadBatchViewModel("T", "folder", [a]);

        a.State = TransferState.Failed;

        Assert.True(batch.HasFailures);
        Assert.Contains("1 failed", batch.TitleText);
    }

    [Fact]
    public void A_batch_is_told_when_a_file_changes_state()
    {
        var a = Item("a.mkv", 1000);
        using var batch = new DownloadBatchViewModel("T", "folder", [a]);

        var raised = new List<string?>();
        batch.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        a.State = TransferState.Completed;

        Assert.Contains(nameof(DownloadBatchViewModel.IsFinished), raised);
    }

    // ---- small text helpers ------------------------------------------------

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(5, "5 min ago")]
    [InlineData(180, "3 h ago")]
    [InlineData(25 * 60, "yesterday")]
    [InlineData(3 * 24 * 60, "3 days ago")]
    public void Added_times_read_naturally(int minutesAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, TorrentViewModel.RelativeTime(now.AddMinutes(-minutesAgo), now));
    }

    [Fact]
    public void The_download_button_says_how_many_files()
    {
        var torrent = Ready(1, hoursAgo: 1);
        Assert.Equal("Pick at least one file", torrent.DownloadButtonText);

        torrent.Tree = FileNodeViewModel.BuildTree("T",
        [
            new MagnetFileNode { Name = "a.mkv", Size = 1, Link = "https://alldebrid.com/f/a" },
            new MagnetFileNode { Name = "b.srt", Size = 1, Link = "https://alldebrid.com/f/b" }
        ]);

        Assert.Equal("Download 2 files", torrent.DownloadButtonText);
        Assert.StartsWith("2 files", torrent.FileSummaryText);
    }

    // ---- plumbing ----------------------------------------------------------

    private async Task<List<UploadedMagnet>> UploadAsync(string body)
    {
        using var client = new AllDebridClient(_log.Logger, FakeHandler.Json(body),
            new ApiThrottle(1000, 100000))
        {
            ApiKey = "TESTKEY0000000000000"
        };

        return await client.UploadMagnetsAsync(["magnet:?xt=urn:btih:" + new string('1', 40)]);
    }

    /// <summary>Same reflection route as TorrentOrderingTests: the add path is private.</summary>
    private static void InvokeHandleUploadResults(MainViewModel vm, List<UploadedMagnet> results)
    {
        var method = typeof(MainViewModel).GetMethod(
            "HandleUploadResults",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(method);
        method.Invoke(vm, [results, "magnet"]);
    }
}
