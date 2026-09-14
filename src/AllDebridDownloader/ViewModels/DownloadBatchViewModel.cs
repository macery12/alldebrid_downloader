using System.ComponentModel;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Models;

namespace AllDebridDownloader.ViewModels;

/// <summary>
/// The files one Download click queued, followed on the wizard's last step. It watches the
/// same TransferItems the Downloads tab shows, so the two can never disagree.
/// </summary>
public sealed class DownloadBatchViewModel : ObservableObject, IDisposable
{
    public DownloadBatchViewModel(string torrentName, string folder, IReadOnlyList<TransferItem> items)
    {
        TorrentName = torrentName;
        Folder = folder;
        Items = items;

        foreach (var item in items) item.PropertyChanged += OnItemChanged;
    }

    public string TorrentName { get; }
    public string Folder { get; }
    public IReadOnlyList<TransferItem> Items { get; }

    public long TotalBytes => Items.Where(t => t.State != TransferState.Skipped)
        .Sum(t => t.TotalBytes > 0 ? t.TotalBytes : t.ExpectedSize);

    public long DownloadedBytes => Items.Sum(t => t.DownloadedBytes);
    public double BytesPerSecond => Items.Where(t => t.IsActive).Sum(t => t.BytesPerSecond);
    public int FinishedCount => Items.Count(t => t.IsFinished);
    public int FailedCount => Items.Count(t => t.State == TransferState.Failed);
    public bool IsFinished => Items.All(t => t.IsFinished);
    public bool HasFailures => FailedCount > 0;

    public double Percent
    {
        get
        {
            var total = TotalBytes;
            if (total > 0) return Math.Clamp(DownloadedBytes * 100.0 / total, 0, 100);
            return IsFinished ? 100 : 0;
        }
    }

    // Floored, so a batch that is 99.6% done does not claim 100% while a file is still open.
    public string PercentText => Math.Floor(Percent).ToString("0") + "%";

    public string ProgressText =>
        ByteFormatter.Format(DownloadedBytes) + " of " + ByteFormatter.Format(TotalBytes);

    public string SpeedText => IsFinished ? "" : ByteFormatter.FormatRate(BytesPerSecond);

    public string TitleText
    {
        get
        {
            var files = Files(Items.Count);
            if (!IsFinished) return "Downloading " + files + " from " + TorrentName;

            return HasFailures
                ? files + " from " + TorrentName + " finished, " + FailedCount + " failed"
                : "Downloaded " + files + " from " + TorrentName;
        }
    }

    public string EtaText => IsFinished
        ? HasFailures ? "Retry failed files from the Downloads tab" : "Done"
        : "ETA " + TimeFormatter.FormatEta(TimeFormatter.EstimateEta(
            Math.Max(0, TotalBytes - DownloadedBytes), BytesPerSecond));

    public void Refresh() => OnPropertiesChanged(
        nameof(TotalBytes), nameof(DownloadedBytes), nameof(BytesPerSecond),
        nameof(FinishedCount), nameof(FailedCount), nameof(IsFinished), nameof(HasFailures),
        nameof(Percent), nameof(PercentText), nameof(ProgressText), nameof(SpeedText),
        nameof(TitleText), nameof(EtaText));

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Byte counts arrive on the manager's UI tick, which calls Refresh. A state change
        // (finished, failed, skipped) can land between ticks, so catch that here.
        if (e.PropertyName == nameof(TransferItem.State)) OnUi(Refresh);
    }

    private static string Files(int count) => count + (count == 1 ? " file" : " files");

    public void Dispose()
    {
        foreach (var item in Items) item.PropertyChanged -= OnItemChanged;
    }
}
