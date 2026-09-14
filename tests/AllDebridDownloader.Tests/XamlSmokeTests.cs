using System.Windows;
using AllDebridDownloader.Models;
using AllDebridDownloader.Services;
using AllDebridDownloader.ViewModels;
using AllDebridDownloader.Views;

namespace AllDebridDownloader.Tests;

/// <summary>
/// XAML errors are runtime errors -- a bad StaticResource key or a malformed binding
/// compiles happily and then throws when the window is constructed. These tests build
/// every window for real on an STA thread so a typo cannot reach a release build.
/// </summary>
public sealed class XamlSmokeTests
{
    /// <summary>
    /// Runs <paramref name="action"/> on a dedicated STA thread with an Application whose
    /// resources are loaded from App.xaml, which is what StaticResource lookups need.
    /// </summary>
    private static void OnStaThread(Action<Application> action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current as App ?? new App();
                app.InitializeComponent();   // loads Themes/Dark.xaml and the converters
                action(app);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The STA thread did not finish.");

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(
                "XAML failed to load: " + failure.GetType().Name + ": " + failure.Message
                + Environment.NewLine + failure.StackTrace);
    }

    [Fact]
    public void The_theme_defines_every_resource_the_views_reference()
    {
        OnStaThread(app =>
        {
            // Brushes and styles referenced by StaticResource across the views.
            string[] required =
            [
                "Bg", "Surface", "SurfaceAlt", "BorderBrushBase", "Text", "Muted",
                "Accent", "AccentHover", "Success", "Warn", "Error",
                "Heading", "SubtleText", "Card", "PrimaryButton", "LinkButton",
                "MenuTabs", "MenuTabItem", "TorrentCard", "ChipButton", "SegmentRadio",
                "BoolToVis", "BoolToVisInverted", "ByteSize", "StateBrush", "LogBrush",
                "NodeGlyph"
            ];

            foreach (var key in required)
                Assert.True(app.TryFindResource(key) is not null, "Missing resource: " + key);
        });
    }

    [Fact]
    public void Main_window_loads_and_binds_against_a_real_view_model()
    {
        OnStaThread(_ =>
        {
            using var log = new TestLogger();
            using var temp = new TempDir();

            var config = new ConfigService(log.Logger, temp.Path);
            config.Load();
            config.Current.DownloadDirectory = temp.Path;

            using var vm = new MainViewModel(log.Logger, config);

            var window = new MainWindow { DataContext = vm };
            window.UseWindowState(() => config.Current.WindowState);

            // Realise the visual tree without showing anything on screen: this is what
            // actually evaluates the bindings and resource references.
            window.Measure(new Size(1180, 800));
            window.Arrange(new Rect(0, 0, 1180, 800));

            Assert.Equal("AllDebrid Downloader", window.Title);
        });
    }

    [Fact]
    public void Main_window_renders_with_a_populated_torrent_and_file_tree()
    {
        OnStaThread(_ =>
        {
            using var log = new TestLogger();
            using var temp = new TempDir();

            var config = new ConfigService(log.Logger, temp.Path);
            config.Load();
            config.Current.DownloadDirectory = temp.Path;

            using var vm = new MainViewModel(log.Logger, config);

            var torrent = new TorrentViewModel(123)
            {
                Name = "Some.Show.S01",
                Hash = "842783e3005495d5d1637f5364b59343c7844707",
                Size = 42_700_000_000,
                Kind = MagnetStatusKind.Ready,
                StatusText = "Ready"
            };

            torrent.Tree = FileNodeViewModel.BuildTree("Some.Show.S01",
            [
                new MagnetFileNode
                {
                    Name = "Season 1",
                    Entries =
                    [
                        new MagnetFileNode { Name = "S01E01.mkv", Size = 2_100_000_000,
                            Link = "https://alldebrid.com/f/a" },
                        new MagnetFileNode { Name = "Subs", Entries =
                        [
                            new MagnetFileNode { Name = "English.srt", Size = 41_000,
                                Link = "https://alldebrid.com/f/b" }
                        ]}
                    ]
                }
            ]);

            vm.Torrents.Add(torrent);
            vm.SelectedTorrent = torrent;

            vm.Downloads.Transfers.Add(new TransferItem
            {
                SourceLink = "https://alldebrid.com/f/a",
                FinalPath = Path.Combine(temp.Path, "Some.Show.S01", "S01E01.mkv"),
                FileName = "S01E01.mkv",
                RelativePath = Path.Combine("Some.Show.S01", "S01E01.mkv"),
                MagnetId = 123,
                TorrentName = "Some.Show.S01",
                ExpectedSize = 2_100_000_000
            });

            var window = new MainWindow { DataContext = vm };
            window.UseWindowState(() => config.Current.WindowState);

            window.Measure(new Size(1180, 800));
            window.Arrange(new Rect(0, 0, 1180, 800));

            // The destination preview is the "each torrent in its own folder" promise,
            // rendered for the user before they commit.
            Assert.Contains("Some.Show.S01", vm.DestinationPreview);
            Assert.Equal(2, torrent.SelectedFileCount);
        });
    }

    [Fact]
    public void Every_tab_realises_its_content()
    {
        // A TabControl only builds the selected tab, so measuring the window once left
        // Downloads, Settings and Log completely unverified -- which is how a pile of
        // unstyled ComboBoxes on the Settings tab went unnoticed.
        OnStaThread(app =>
        {
            using var log = new TestLogger();
            using var temp = new TempDir();

            var config = new ConfigService(log.Logger, temp.Path);
            config.Load();
            config.Current.DownloadDirectory = temp.Path;

            using var vm = new MainViewModel(log.Logger, config);

            var window = new MainWindow { DataContext = vm };
            window.UseWindowState(() => config.Current.WindowState);

            Realize(window);
            try
            {
                var tabs = FindVisualChild<System.Windows.Controls.TabControl>(window);
                Assert.NotNull(tabs);
                Assert.Equal(4, tabs.Items.Count);

                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;

                    // Force the newly selected tab's content to be built and laid out.
                    window.UpdateLayout();
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Every_home_step_realises_its_content()
    {
        // Each wizard step and the classic view is collapsed until it is reached, so a bad
        // binding in one of them would otherwise only show up when a user got there.
        OnStaThread(_ =>
        {
            using var log = new TestLogger();
            using var temp = new TempDir();

            var config = new ConfigService(log.Logger, temp.Path);
            config.Load();
            config.Current.DownloadDirectory = temp.Path;

            using var vm = new MainViewModel(log.Logger, config);

            var ready = new TorrentViewModel(123)
            {
                Name = "Some.Show.S01",
                Size = 42_700_000_000,
                Kind = MagnetStatusKind.Ready,
                StatusText = "Ready",
                FilesRequested = true,
                AddedAt = DateTimeOffset.Now.AddHours(-2)
            };
            ready.Tree = FileNodeViewModel.BuildTree("Some.Show.S01",
            [
                new MagnetFileNode { Name = "S01E01.mkv", Size = 2_100_000_000,
                    Link = "https://alldebrid.com/f/a" }
            ]);

            vm.Torrents.Add(ready);
            vm.Torrents.Add(new TorrentViewModel(456)
            {
                Name = "Still.Fetching", Kind = MagnetStatusKind.Processing,
                StatusText = "Downloading", Size = 1000, Downloaded = 630
            });

            var window = new MainWindow { DataContext = vm };
            window.UseWindowState(() => config.Current.WindowState);

            Realize(window);
            try
            {
                Assert.True(vm.ShowPickStep);
                window.UpdateLayout();

                vm.OpenTorrent(ready);
                window.UpdateLayout();
                Assert.True(vm.ShowFilesStep);

                var item = new TransferItem
                {
                    SourceLink = "https://alldebrid.com/f/a",
                    FinalPath = Path.Combine(temp.Path, "Some.Show.S01", "S01E01.mkv"),
                    FileName = "S01E01.mkv",
                    RelativePath = Path.Combine("Some.Show.S01", "S01E01.mkv"),
                    MagnetId = 123,
                    TorrentName = "Some.Show.S01",
                    ExpectedSize = 2_100_000_000
                };
                vm.ShowStarted(new DownloadBatchViewModel("Some.Show.S01",
                    Path.Combine(temp.Path, "Some.Show.S01"), [item]));
                window.UpdateLayout();
                Assert.True(vm.ShowStartedStep);

                vm.IsClassicView = true;
                window.UpdateLayout();

                vm.IsClassicView = false;
                vm.GoHome();
                window.UpdateLayout();
                Assert.True(vm.ShowPickStep);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Settings_dropdowns_use_the_dark_template_rather_than_the_system_one()
    {
        OnStaThread(_ =>
        {
            using var log = new TestLogger();
            using var temp = new TempDir();

            var config = new ConfigService(log.Logger, temp.Path);
            config.Load();
            config.Current.DownloadDirectory = temp.Path;

            using var vm = new MainViewModel(log.Logger, config);

            var window = new MainWindow { DataContext = vm };
            window.UseWindowState(() => config.Current.WindowState);

            Realize(window);
            try
            {
                var tabs = FindVisualChild<System.Windows.Controls.TabControl>(window);
                Assert.NotNull(tabs);

                // Settings is the third tab.
                tabs.SelectedIndex = 2;
                window.UpdateLayout();

                var combo = FindVisualChild<System.Windows.Controls.ComboBox>(window);
                Assert.NotNull(combo);
                combo.ApplyTemplate();

                // The templated popup border is named in the dark theme; the stock WPF
                // template has no such part, so finding it proves the theme applied and
                // the dropdown is not falling back to the light system template.
                Assert.NotNull(combo.Template.FindName("PopupBorder", combo));
                Assert.NotNull(combo.Template.FindName("PART_Popup", combo));

                // And the selection actually round-trips, so the box is not blank.
                Assert.NotNull(combo.SelectedItem);
                Assert.Equal(config.Current.MaxConcurrentFiles, vm.Settings.MaxConcurrentFiles);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Measure/Arrange alone does not build a Window's visual tree -- its template is
    /// only applied once it is shown. Shows it far off-screen so the tree is real.
    /// </summary>
    private static void Realize(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        window.UpdateLayout();
    }

    /// <summary>Depth-first search of the visual tree for the first T.</summary>
    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;

            var deeper = FindVisualChild<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    [Fact]
    public void Main_window_survives_a_narrow_layout()
    {
        OnStaThread(_ =>
        {
            using var log = new TestLogger();
            using var temp = new TempDir();

            var config = new ConfigService(log.Logger, temp.Path);
            config.Load();

            using var vm = new MainViewModel(log.Logger, config);

            var window = new MainWindow { DataContext = vm };
            window.UseWindowState(() => config.Current.WindowState);

            // The documented minimum size.
            window.Measure(new Size(900, 600));
            window.Arrange(new Rect(0, 0, 900, 600));
        });
    }

    [Fact]
    public void Connect_dialog_loads()
    {
        OnStaThread(_ =>
        {
            using var log = new TestLogger();
            using var client = new AllDebridClient(log.Logger,
                FakeHandler.Json("""{"status":"success","data":{"pin":"ABCD","check":"c","expires_in":600}}"""),
                new ApiThrottle(1000, 100000));

            var dialog = new ConnectDialog(client, log.Logger);

            dialog.Measure(new Size(520, 700));
            dialog.Arrange(new Rect(0, 0, 520, 700));

            Assert.Equal("Connect to AllDebrid", dialog.Title);
        });
    }

    [Fact]
    public void Download_folder_dialog_loads_and_shows_an_example_path()
    {
        OnStaThread(_ =>
        {
            var dialog = new DownloadFolderDialog("D:\\Downloads\\AllDebrid",
                "The folder that was saved is not available any more.");

            dialog.Measure(new Size(560, 500));
            dialog.Arrange(new Rect(0, 0, 560, 500));

            Assert.Equal("Where should downloads go?", dialog.Title);
        });
    }

    [Fact]
    public void File_conflict_dialog_loads()
    {
        OnStaThread(_ =>
        {
            var dialog = new FileConflictDialog(new ConflictRequest
            {
                FinalPath = "D:\\Downloads\\a.mkv",
                FileName = "S01E01.mkv",
                ExistingSize = 2_100_000_000,
                ExistingModified = new DateTime(2026, 4, 2, 18, 31, 0),
                IncomingSize = 2_100_000_000
            });

            dialog.Measure(new Size(520, 400));
            dialog.Arrange(new Rect(0, 0, 520, 400));

            Assert.Equal(ConflictChoice.Cancel, dialog.Choice);   // nothing chosen yet
        });
    }

    [Fact]
    public void Resume_prompt_dialog_loads_with_several_items()
    {
        OnStaThread(_ =>
        {
            var root = "D:\\Downloads\\AllDebrid";

            var items = new List<(string, PartSidecar)>();
            for (var i = 1; i <= 3; i++)
            {
                var sidecar = new PartSidecar
                {
                    SourceLink = "https://alldebrid.com/f/" + i,
                    TotalSize = 1000,
                    Segments = SidecarStore.PlanSegments(1000, 1)
                };
                sidecar.Segments[0].Written = i * 200;
                items.Add((Path.Combine(root, "Show", "S01E0" + i + ".mkv"), sidecar));
            }

            var dialog = new ResumePromptDialog(items, root);

            dialog.Measure(new Size(560, 520));
            dialog.Arrange(new Rect(0, 0, 560, 520));

            Assert.Equal(ResumeDecision.NotNow, dialog.Decision);   // default is safe
        });
    }
}
