using System.Text.Json;
using AllDebridDownloader.Models;
using AllDebridDownloader.ViewModels;

namespace AllDebridDownloader.Tests;

public sealed class FileTreeTests
{
    private static List<MagnetFileNode> Parse(string json) =>
        JsonSerializer.Deserialize<List<MagnetFileNode>>(json, ApiJson.Options)!;

    // -----------------------------------------------------------------------
    // Shapes from the documentation
    // -----------------------------------------------------------------------

    [Fact]
    public void Single_file_at_root()
    {
        var nodes = Parse("""
        [{"n":"some.file.avi","s":45466546,"l":"https://alldebrid.com/f/4564654"}]
        """);

        var root = FileNodeViewModel.BuildTree("MyTorrent", nodes);

        Assert.Single(root.Children);
        var file = root.Children[0];
        Assert.False(file.IsFolder);
        Assert.Equal("some.file.avi", file.Name);
        Assert.Equal(45466546, file.FileSize);
        Assert.Equal("https://alldebrid.com/f/4564654", file.Link);
    }

    [Fact]
    public void Single_file_in_a_subfolder()
    {
        var nodes = Parse("""
        [{"n":"subfolderName","e":[
          {"n":"some.file.avi","s":45466546,"l":"https://alldebrid.com/f/4564654"}]}]
        """);

        var root = FileNodeViewModel.BuildTree("MyTorrent", nodes);

        var folder = root.Children[0];
        Assert.True(folder.IsFolder);
        Assert.Equal("subfolderName", folder.Name);
        Assert.Single(folder.Children);
        Assert.False(folder.Children[0].IsFolder);
    }

    [Fact]
    public void Deeply_nested_folders()
    {
        var nodes = Parse("""
        [{"n":"a","e":[{"n":"b","e":[{"n":"c","e":[
          {"n":"deep.mkv","s":100,"l":"https://alldebrid.com/f/x"}]}]}]}]
        """);

        var root = FileNodeViewModel.BuildTree("T", nodes);

        var leaf = root.Children[0].Children[0].Children[0].Children[0];
        Assert.Equal("deep.mkv", leaf.Name);
        Assert.Equal(["a", "b", "c", "deep.mkv"], leaf.PathSegments());
    }

    [Fact]
    public void Mixed_root_files_and_folders()
    {
        var nodes = Parse("""
        [
          {"n":"subfolderName","e":[
            {"n":"deepSubfolder","e":[{"n":"some.file.txt","s":456546,"l":"https://a/1"}]},
            {"n":"otherSubfolder","e":[{"n":"other.file.txt","s":12211,"l":"https://a/2"}]}
          ]},
          {"n":"file.at.root.avi","s":1000000000,"l":"https://a/3"}
        ]
        """);

        var root = FileNodeViewModel.BuildTree("T", nodes);

        Assert.Equal(2, root.Children.Count);
        Assert.True(root.Children[0].IsFolder);
        Assert.False(root.Children[1].IsFolder);
        Assert.Equal(3, root.AllFiles().Count());
    }

    [Fact]
    public void Folder_sizes_roll_up_from_children()
    {
        var nodes = Parse("""
        [{"n":"Season 1","e":[
          {"n":"a.mkv","s":100,"l":"https://a/1"},
          {"n":"b.mkv","s":250,"l":"https://a/2"},
          {"n":"Subs","e":[{"n":"en.srt","s":7,"l":"https://a/3"}]}
        ]}]
        """);

        var root = FileNodeViewModel.BuildTree("T", nodes);

        Assert.Equal(357, root.TotalSize);
        Assert.Equal(357, root.Children[0].TotalSize);
        Assert.Equal(7, root.Children[0].Children[2].TotalSize);
    }

    [Fact]
    public void An_empty_entries_array_is_a_folder_with_no_children()
    {
        var nodes = Parse("""[{"n":"EmptyFolder","e":[]}]""");
        var root = FileNodeViewModel.BuildTree("T", nodes);

        Assert.True(root.Children[0].IsFolder);
        Assert.Empty(root.Children[0].Children);
        Assert.Empty(root.AllFiles());
        Assert.Equal(0, root.TotalSize);
    }

    [Fact]
    public void An_empty_file_list_yields_an_empty_tree()
    {
        var root = FileNodeViewModel.BuildTree("T", []);

        Assert.Empty(root.Children);
        Assert.Empty(root.SelectedFiles());
    }

    [Fact]
    public void Duplicate_names_at_one_level_are_both_kept()
    {
        var nodes = Parse("""
        [{"n":"dup.mkv","s":1,"l":"https://a/1"},{"n":"dup.mkv","s":2,"l":"https://a/2"}]
        """);

        var root = FileNodeViewModel.BuildTree("T", nodes);
        Assert.Equal(2, root.Children.Count);
    }

    [Fact]
    public void A_node_with_no_name_gets_a_placeholder()
    {
        var nodes = Parse("""[{"s":1,"l":"https://a/1"}]""");
        var root = FileNodeViewModel.BuildTree("T", nodes);

        Assert.Equal("unnamed", root.Children[0].Name);
    }

    [Fact]
    public void Folders_are_identified_by_entries_not_by_null_size()
    {
        // A file can legitimately have a null size; that must not make it a folder.
        var nodes = Parse("""[{"n":"odd.mkv","l":"https://a/1"}]""");
        var root = FileNodeViewModel.BuildTree("T", nodes);

        Assert.False(root.Children[0].IsFolder);
        Assert.Equal(0, root.Children[0].FileSize);
    }

    // -----------------------------------------------------------------------
    // Tri-state selection
    // -----------------------------------------------------------------------

    private static FileNodeViewModel ThreeFileTree() => FileNodeViewModel.BuildTree("T", Parse("""
        [{"n":"Season 1","e":[
          {"n":"a.mkv","s":100,"l":"https://a/1"},
          {"n":"b.mkv","s":200,"l":"https://a/2"}
        ]},
         {"n":"root.txt","s":5,"l":"https://a/3"}]
        """));

    [Fact]
    public void Everything_starts_selected()
    {
        var root = ThreeFileTree();

        Assert.True(root.IsSelected);
        Assert.Equal(3, root.SelectedFiles().Count());
        Assert.Equal(305, root.SelectedFiles().Sum(f => f.FileSize));
    }

    [Fact]
    public void Deselecting_one_child_makes_the_parent_indeterminate()
    {
        var root = ThreeFileTree();
        var season = root.Children[0];

        season.Children[0].IsSelected = false;

        Assert.Null(season.IsSelected);
        Assert.Null(root.IsSelected);
        Assert.Equal(2, root.SelectedFiles().Count());
    }

    [Fact]
    public void Deselecting_every_child_makes_the_parent_false()
    {
        var root = ThreeFileTree();
        var season = root.Children[0];

        season.Children[0].IsSelected = false;
        season.Children[1].IsSelected = false;

        Assert.False(season.IsSelected);
        Assert.Null(root.IsSelected);   // root.txt is still selected
    }

    [Fact]
    public void Reselecting_the_last_sibling_flips_the_parent_back_to_true()
    {
        var root = ThreeFileTree();
        var season = root.Children[0];

        season.Children[0].IsSelected = false;
        Assert.Null(season.IsSelected);

        season.Children[0].IsSelected = true;
        Assert.True(season.IsSelected);
        Assert.True(root.IsSelected);
    }

    [Fact]
    public void Deselecting_a_folder_deselects_all_its_descendants()
    {
        var root = FileNodeViewModel.BuildTree("T", Parse("""
        [{"n":"a","e":[{"n":"b","e":[
          {"n":"x.mkv","s":1,"l":"https://a/1"},
          {"n":"y.mkv","s":2,"l":"https://a/2"}]}]}]
        """));

        root.Children[0].IsSelected = false;

        Assert.False(root.Children[0].Children[0].IsSelected);
        Assert.False(root.Children[0].Children[0].Children[0].IsSelected);
        Assert.Empty(root.SelectedFiles());
    }

    [Fact]
    public void Selecting_a_folder_selects_all_its_descendants()
    {
        var root = ThreeFileTree();
        root.SetSelectedRecursive(false);
        Assert.Empty(root.SelectedFiles());

        root.Children[0].IsSelected = true;

        Assert.Equal(2, root.SelectedFiles().Count());
        Assert.Null(root.IsSelected);
    }

    [Fact]
    public void Clicking_an_indeterminate_folder_selects_everything_under_it()
    {
        var root = ThreeFileTree();
        var season = root.Children[0];

        season.Children[0].IsSelected = false;
        Assert.Null(season.IsSelected);

        // WPF hands a three-state checkbox null on the next click; that must mean "all".
        season.IsSelected = null;

        Assert.True(season.IsSelected);
        Assert.Equal(3, root.SelectedFiles().Count());
    }

    [Fact]
    public void Selection_changed_fires_once_at_the_root()
    {
        var root = ThreeFileTree();
        var count = 0;
        root.SelectionChanged += () => count++;

        root.Children[0].Children[0].IsSelected = false;

        Assert.Equal(1, count);
    }

    [Fact]
    public void Selected_files_skips_nodes_with_no_link()
    {
        var root = FileNodeViewModel.BuildTree("T", Parse("""
        [{"n":"nolink.mkv","s":10},{"n":"ok.mkv","s":20,"l":"https://a/1"}]
        """));

        // A file with no link cannot be downloaded, so it is not offered.
        Assert.Single(root.SelectedFiles());
        Assert.Equal("ok.mkv", root.SelectedFiles().First().Name);
    }

    [Fact]
    public void Video_only_selects_video_files_and_leaves_folders_indeterminate()
    {
        var root = FileNodeViewModel.BuildTree("T", Parse("""
        [{"n":"Season 1","e":[
          {"n":"a.mkv","s":100,"l":"https://a/1"},
          {"n":"a.srt","s":2,"l":"https://a/2"},
          {"n":"b.mp4","s":300,"l":"https://a/3"},
          {"n":"readme.nfo","s":1,"l":"https://a/4"}
        ]}]
        """));

        root.SelectVideoOnly();

        var season = root.Children[0];
        Assert.True(season.Children[0].IsSelected);    // .mkv
        Assert.False(season.Children[1].IsSelected);   // .srt
        Assert.True(season.Children[2].IsSelected);    // .mp4
        Assert.False(season.Children[3].IsSelected);   // .nfo

        Assert.Null(season.IsSelected);
        Assert.Equal(2, root.SelectedFiles().Count());
        Assert.Equal(400, root.SelectedFiles().Sum(f => f.FileSize));
    }

    [Theory]
    [InlineData("a.mkv", true)]
    [InlineData("a.MP4", true)]
    [InlineData("a.avi", true)]
    [InlineData("a.webm", true)]
    [InlineData("a.srt", false)]
    [InlineData("a.nfo", false)]
    [InlineData("a", false)]
    public void Video_detection_is_extension_based_and_case_insensitive(string name, bool isVideo)
    {
        var node = new FileNodeViewModel(name, isFolder: false, parent: null);
        Assert.Equal(isVideo, node.IsVideo);
    }

    // -----------------------------------------------------------------------
    // Path segments used to rebuild the folder structure
    // -----------------------------------------------------------------------

    [Fact]
    public void Path_segments_exclude_the_synthetic_root()
    {
        var root = ThreeFileTree();

        Assert.Equal(["Season 1", "a.mkv"], root.Children[0].Children[0].PathSegments());
        Assert.Equal(["root.txt"], root.Children[1].PathSegments());
        Assert.Empty(root.PathSegments());
    }
}
