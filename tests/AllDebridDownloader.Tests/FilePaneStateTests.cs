using AllDebridDownloader.Models;
using AllDebridDownloader.Services;
using AllDebridDownloader.ViewModels;

namespace AllDebridDownloader.Tests;

/// <summary>
/// The file pane stacks four elements in one grid cell, so exactly one must ever be
/// visible. Binding each one through "SelectedTorrent.Something" was the bug: with
/// nothing selected those bindings fail, and a failed Visibility binding falls back to
/// Visible, which drew the empty-state message and "Loading the file list" on top of
/// each other.
/// </summary>
public sealed class FilePaneStateTests : IDisposable
{
    private readonly TestLogger _log = new();
    private readonly TempDir _temp = new();

    public void Dispose()
    {
        _log.Dispose();
        _temp.Dispose();
    }

    private MainViewModel MakeViewModel()
    {
        var config = new ConfigService(_log.Logger, _temp.Path);
        config.Load();
        config.Current.DownloadDirectory = _temp.Path;
        return new MainViewModel(_log.Logger, config);
    }

    private static int VisibleCount(MainViewModel vm) =>
        (vm.ShowFileTree ? 1 : 0)
        + (vm.ShowFileLoading ? 1 : 0)
        + (vm.ShowFileError ? 1 : 0)
        + (vm.ShowFileEmptyState ? 1 : 0);

    /// <summary>
    /// A ready torrent whose files have already been asked for. Selecting a ready torrent
    /// that has NOT been asked for kicks off a real fetch, which is correct behaviour but
    /// would make these pure state assertions race against it.
    /// </summary>
    private static TorrentViewModel ReadyTorrent(long id = 123) => new(id)
    {
        Name = id == 123 ? "Some.Show.S01" : "Torrent " + id,
        Kind = MagnetStatusKind.Ready,
        StatusText = "Ready",
        FilesRequested = true
    };

    [Fact]
    public void Exactly_one_state_is_visible_with_nothing_selected()
    {
        using var vm = MakeViewModel();

        Assert.Equal(1, VisibleCount(vm));
        Assert.True(vm.ShowFileEmptyState);
        Assert.Contains("Select a torrent", vm.FileEmptyMessage);
    }

    [Fact]
    public void Exactly_one_state_is_visible_while_the_file_list_loads()
    {
        using var vm = MakeViewModel();

        var torrent = ReadyTorrent();
        vm.Torrents.Add(torrent);
        vm.SelectedTorrent = torrent;

        torrent.IsLoadingFiles = true;

        // This is the exact overlap that was reported: loading with no tree yet.
        Assert.Equal(1, VisibleCount(vm));
        Assert.True(vm.ShowFileLoading);
        Assert.False(vm.ShowFileEmptyState);
    }

    [Fact]
    public void Exactly_one_state_is_visible_once_the_tree_arrives()
    {
        using var vm = MakeViewModel();

        var torrent = ReadyTorrent();
        vm.Torrents.Add(torrent);
        vm.SelectedTorrent = torrent;

        torrent.IsLoadingFiles = true;
        torrent.Tree = FileNodeViewModel.BuildTree("Some.Show.S01",
        [
            new MagnetFileNode { Name = "a.mkv", Size = 100, Link = "https://alldebrid.com/f/a" }
        ]);
        torrent.IsLoadingFiles = false;

        Assert.Equal(1, VisibleCount(vm));
        Assert.True(vm.ShowFileTree);
    }

    [Fact]
    public void A_tree_wins_over_a_concurrent_reload()
    {
        using var vm = MakeViewModel();

        var torrent = ReadyTorrent();
        vm.Torrents.Add(torrent);
        vm.SelectedTorrent = torrent;

        torrent.Tree = FileNodeViewModel.BuildTree("T",
        [
            new MagnetFileNode { Name = "a.mkv", Size = 1, Link = "https://alldebrid.com/f/a" }
        ]);
        torrent.IsLoadingFiles = true;   // a refresh started while the tree is on screen

        Assert.Equal(1, VisibleCount(vm));
        Assert.True(vm.ShowFileTree);
        Assert.False(vm.ShowFileLoading);
    }

    [Fact]
    public void Exactly_one_state_is_visible_when_the_file_list_fails()
    {
        using var vm = MakeViewModel();

        var torrent = ReadyTorrent();
        vm.Torrents.Add(torrent);
        vm.SelectedTorrent = torrent;

        torrent.FileError = "AllDebrid no longer has that torrent.";

        Assert.Equal(1, VisibleCount(vm));
        Assert.True(vm.ShowFileError);
        Assert.Equal("AllDebrid no longer has that torrent.", vm.FileErrorMessage);
    }

    [Fact]
    public void An_error_does_not_show_while_a_retry_is_loading()
    {
        using var vm = MakeViewModel();

        var torrent = ReadyTorrent();
        vm.Torrents.Add(torrent);
        vm.SelectedTorrent = torrent;

        torrent.FileError = "Could not connect.";
        torrent.IsLoadingFiles = true;

        Assert.Equal(1, VisibleCount(vm));
        Assert.True(vm.ShowFileLoading);
    }

    [Fact]
    public void The_empty_message_explains_the_particular_situation()
    {
        using var vm = MakeViewModel();

        var processing = new TorrentViewModel(1)
        {
            Name = "Downloading.One",
            Kind = MagnetStatusKind.Processing
        };
        vm.Torrents.Add(processing);
        vm.SelectedTorrent = processing;
        Assert.Contains("Waiting for AllDebrid", vm.FileEmptyMessage);

        var failed = new TorrentViewModel(2) { Name = "Bad.One", Kind = MagnetStatusKind.Error };
        vm.Torrents.Add(failed);
        vm.SelectedTorrent = failed;
        Assert.Contains("failed on AllDebrid", vm.FileEmptyMessage);

        vm.SelectedTorrent = null;
        Assert.Contains("Select a torrent", vm.FileEmptyMessage);
    }

    [Fact]
    public void Switching_selection_keeps_the_pane_consistent()
    {
        using var vm = MakeViewModel();

        var withTree = ReadyTorrent();
        withTree.Tree = FileNodeViewModel.BuildTree("T",
        [
            new MagnetFileNode { Name = "a.mkv", Size = 1, Link = "https://alldebrid.com/f/a" }
        ]);

        var loading = ReadyTorrent(456);
        loading.IsLoadingFiles = true;

        vm.Torrents.Add(withTree);
        vm.Torrents.Add(loading);

        vm.SelectedTorrent = withTree;
        Assert.True(vm.ShowFileTree);
        Assert.Equal(1, VisibleCount(vm));

        vm.SelectedTorrent = loading;
        Assert.True(vm.ShowFileLoading);
        Assert.Equal(1, VisibleCount(vm));

        vm.SelectedTorrent = null;
        Assert.True(vm.ShowFileEmptyState);
        Assert.Equal(1, VisibleCount(vm));
    }

    [Fact]
    public void The_pane_stops_following_a_torrent_once_it_is_deselected()
    {
        using var vm = MakeViewModel();

        var first = ReadyTorrent();
        var second = ReadyTorrent(456);

        vm.Torrents.Add(first);
        vm.Torrents.Add(second);

        vm.SelectedTorrent = first;
        vm.SelectedTorrent = second;

        // A late update on the old torrent must not redraw the pane for the new one.
        first.IsLoadingFiles = true;

        Assert.False(vm.ShowFileLoading);
        Assert.True(vm.ShowFileEmptyState);
        Assert.Equal(1, VisibleCount(vm));
    }

    [Fact]
    public void Selecting_a_ready_torrent_whose_files_are_unknown_triggers_a_fetch()
    {
        using var vm = MakeViewModel();

        // The inverse of what ReadyTorrent() suppresses: this is the behaviour that
        // populates the pane in the first place.
        var torrent = new TorrentViewModel(789)
        {
            Name = "Fresh.Torrent",
            Kind = MagnetStatusKind.Ready,
            StatusText = "Ready"
        };

        vm.Torrents.Add(torrent);
        vm.SelectedTorrent = torrent;

        // No API key is configured here, so the fetch runs and fails -- and the recorded
        // error is itself the proof that selecting the torrent kicked it off. A failed
        // attempt also clears FilesRequested so the user can retry with Refresh.
        Assert.NotNull(torrent.FileError);
        Assert.False(torrent.FilesRequested);

        // The pane shows that error rather than the generic empty message.
        Assert.True(vm.ShowFileError);
        Assert.Equal(1, VisibleCount(vm));
    }

    [Fact]
    public void The_destination_preview_is_empty_without_a_selection()
    {
        using var vm = MakeViewModel();

        Assert.False(vm.HasSelection);
        Assert.Equal("", vm.DestinationPreview);

        var torrent = ReadyTorrent();
        vm.Torrents.Add(torrent);
        vm.SelectedTorrent = torrent;

        Assert.True(vm.HasSelection);
        Assert.Contains("Some.Show.S01", vm.DestinationPreview);
    }
}
