using System.Windows;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Models;

namespace AllDebridDownloader.Views;

public enum ResumeDecision { Resume, NotNow, Discard }

/// <summary>
/// Offered at startup when .part.json sidecars are found under the output folder.
/// </summary>
public partial class ResumePromptDialog : Window
{
    private sealed record Row(string Path, string SizeText, string PercentText);

    public ResumePromptDialog(IReadOnlyList<(string FinalPath, PartSidecar Sidecar)> items, string downloadRoot)
    {
        InitializeComponent();

        var totalDone = items.Sum(i => i.Sidecar.DownloadedBytes);

        SummaryText.Text = items.Count == 1
            ? "1 download was interrupted (" + ByteFormatter.Format(totalDone) + " already here)."
            : items.Count + " downloads were interrupted ("
              + ByteFormatter.Format(totalDone) + " already here).";

        ItemList.ItemsSource = items.Select(i =>
        {
            var relative = PathHelper.IsInside(downloadRoot, i.FinalPath)
                ? Path.GetRelativePath(downloadRoot, i.FinalPath)
                : Path.GetFileName(i.FinalPath);

            var percent = i.Sidecar.TotalSize > 0
                ? (i.Sidecar.DownloadedBytes * 100.0 / i.Sidecar.TotalSize).ToString("0") + "%"
                : "—";

            return new Row(relative, ByteFormatter.Format(i.Sidecar.TotalSize), percent);
        }).ToList();
    }

    public ResumeDecision Decision { get; private set; } = ResumeDecision.NotNow;

    private void OnResume(object sender, RoutedEventArgs e) => Finish(ResumeDecision.Resume);
    private void OnNotNow(object sender, RoutedEventArgs e) => Finish(ResumeDecision.NotNow);

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Delete the partial files and their resume state?",
            "Discard unfinished downloads",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes) Finish(ResumeDecision.Discard);
    }

    private void Finish(ResumeDecision decision)
    {
        Decision = decision;
        DialogResult = true;
        Close();
    }
}
