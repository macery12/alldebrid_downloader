using System.Windows;
using Microsoft.Win32;
using AllDebridDownloader.Services;

namespace AllDebridDownloader.Views;

/// <summary>
/// First-run output folder picker. Also reused when the saved folder has gone away
/// (an unplugged drive, say), in which case a banner explains why it reappeared.
/// </summary>
public partial class DownloadFolderDialog : Window
{
    public DownloadFolderDialog(string initialPath, string? banner = null)
    {
        InitializeComponent();

        PathBox.Text = initialPath;

        if (!string.IsNullOrWhiteSpace(banner))
        {
            BannerText.Text = banner;
            Banner.Visibility = Visibility.Visible;
        }

        UpdateExample();
        Loaded += (_, _) =>
        {
            PathBox.Focus();
            PathBox.CaretIndex = PathBox.Text.Length;
        };
    }

    public string? SelectedPath { get; private set; }

    private void OnPathChanged(object sender, RoutedEventArgs e)
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        UpdateExample();
    }

    private void UpdateExample()
    {
        if (ExampleText is null) return;

        var root = PathBox.Text.Trim().TrimEnd('\\', '/');
        if (root.Length == 0) root = "D:\\Downloads\\AllDebrid";

        ExampleText.Text =
            root + "\\Some.Show.S01\\Season 1\\S01E01.mkv" + Environment.NewLine +
            root + "\\ubuntu-24.10-desktop-amd64\\ubuntu-24.10-desktop-amd64.iso";
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the download folder",
            Multiselect = false
        };

        var current = PathBox.Text.Trim();
        if (current.Length > 0)
        {
            try
            {
                // Start in the nearest existing ancestor, not a folder that isn't there.
                var probe = current;
                while (probe.Length > 0 && !Directory.Exists(probe))
                    probe = Path.GetDirectoryName(probe) ?? string.Empty;

                if (probe.Length > 0) dialog.InitialDirectory = probe;
            }
            catch
            {
                // A malformed path just means no initial directory.
            }
        }

        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FolderName;
    }

    private void OnUse(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();

        if (!ConfigService.IsUsableDownloadDirectory(path, out var problem))
        {
            ErrorText.Text = problem ?? "That folder cannot be used.";
            ErrorBanner.Visibility = Visibility.Visible;
            return;
        }

        SelectedPath = Path.GetFullPath(path);
        DialogResult = true;
        Close();
    }
}
