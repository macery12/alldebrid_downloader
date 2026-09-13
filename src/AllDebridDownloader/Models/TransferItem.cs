using AllDebridDownloader.Helpers;
using AllDebridDownloader.Services;
using AllDebridDownloader.ViewModels;

namespace AllDebridDownloader.Models;

public enum TransferState
{
    Queued,
    Resolving,
    Downloading,
    Retrying,
    Paused,
    Completed,
    Failed,
    Cancelled,
    Skipped
}

/// <summary>
/// One file in the download queue. This doubles as the row view model -- the Downloads
/// grid binds straight to it, rather than having a parallel wrapper type that only
/// forwards properties.
/// </summary>
public sealed class TransferItem : ObservableObject
{
    private readonly RateMeter _meter = new();

    private TransferState _state = TransferState.Queued;
    private long _downloadedBytes;
    private long _totalBytes;
    private string? _errorMessage;
    private string? _technicalDetails;
    private int _attempt;
    private int _activeConnections;

    public required string SourceLink { get; init; }
    public required string FinalPath { get; init; }
    public required string FileName { get; init; }

    /// <summary>Path relative to the torrent folder, for display.</summary>
    public required string RelativePath { get; init; }

    public required long MagnetId { get; init; }
    public required string TorrentName { get; init; }

    /// <summary>Size reported by the API, before the HTTP probe confirms it.</summary>
    public long ExpectedSize { get; init; }

    public string PartPath => SidecarStore.PartPathFor(FinalPath);

    public CancellationTokenSource? Cancellation { get; set; }

    /// <summary>Set when the user pauses, so the manager can tell pause from cancel.</summary>
    public bool PauseRequested { get; set; }

    public TransferState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertiesChanged(nameof(StateText), nameof(IsActive), nameof(IsFinished),
                nameof(CanPause), nameof(CanResume), nameof(CanCancel), nameof(CanRetry));
            if (value is not TransferState.Downloading) _meter.Reset();
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (!SetProperty(ref _downloadedBytes, value)) return;
            OnPropertiesChanged(nameof(ProgressPercent), nameof(ProgressText),
                nameof(RemainingBytes), nameof(EtaText));
        }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (!SetProperty(ref _totalBytes, value)) return;
            OnPropertiesChanged(nameof(ProgressPercent), nameof(ProgressText), nameof(SizeText),
                nameof(RemainingBytes), nameof(EtaText), nameof(HasKnownSize));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(StateText)); }
    }

    public string? TechnicalDetails
    {
        get => _technicalDetails;
        set => SetProperty(ref _technicalDetails, value);
    }

    public int Attempt
    {
        get => _attempt;
        set { if (SetProperty(ref _attempt, value)) OnPropertyChanged(nameof(StateText)); }
    }

    public int ActiveConnections
    {
        get => _activeConnections;
        set { if (SetProperty(ref _activeConnections, value)) OnPropertyChanged(nameof(StateText)); }
    }

    // ---- computed, for binding ------------------------------------------------

    public bool HasKnownSize => TotalBytes > 0;

    public long RemainingBytes => TotalBytes > 0 ? Math.Max(0, TotalBytes - DownloadedBytes) : 0;

    public double ProgressPercent => TotalBytes > 0
        ? Math.Clamp(DownloadedBytes * 100.0 / TotalBytes, 0, 100)
        : 0;

    public string ProgressText => TotalBytes > 0
        ? ProgressPercent.ToString("0") + "%"
        : DownloadedBytes > 0 ? ByteFormatter.Format(DownloadedBytes) : "—";

    public string SizeText => ByteFormatter.Format(TotalBytes > 0 ? TotalBytes : ExpectedSize);

    public double BytesPerSecond => State == TransferState.Downloading ? _meter.BytesPerSecond : 0;

    public string SpeedText => State == TransferState.Downloading
        ? ByteFormatter.FormatRate(BytesPerSecond)
        : "—";

    public string EtaText => State == TransferState.Downloading
        ? TimeFormatter.FormatEta(TimeFormatter.EstimateEta(RemainingBytes, BytesPerSecond))
        : "—";

    public string StateText => State switch
    {
        TransferState.Queued => "Queued",
        TransferState.Resolving => "Getting link",
        TransferState.Downloading => ActiveConnections > 1
            ? ActiveConnections + " conns"
            : "Downloading",
        TransferState.Retrying => "Retrying (" + Attempt + "/5)",
        TransferState.Paused => "Paused",
        TransferState.Completed => "Done",
        TransferState.Failed => ErrorMessage is null ? "Failed" : "Failed: " + Truncate(ErrorMessage, 60),
        TransferState.Cancelled => "Cancelled",
        TransferState.Skipped => "Skipped",
        _ => State.ToString()
    };

    public bool IsActive => State is TransferState.Resolving or TransferState.Downloading
        or TransferState.Retrying;

    public bool IsFinished => State is TransferState.Completed or TransferState.Failed
        or TransferState.Cancelled or TransferState.Skipped;

    public bool CanPause => IsActive || State == TransferState.Queued;
    public bool CanResume => State is TransferState.Paused or TransferState.Failed
        or TransferState.Cancelled;
    public bool CanCancel => IsActive || State is TransferState.Queued or TransferState.Paused;
    public bool CanRetry => State is TransferState.Failed or TransferState.Cancelled;

    // ---- progress plumbing ---------------------------------------------------

    /// <summary>Record received bytes. Called from download worker threads.</summary>
    public void ReportBytes(long bytes)
    {
        if (bytes <= 0) return;
        _meter.Add(bytes);
        Interlocked.Add(ref _downloadedBytes, bytes);
    }

    /// <summary>
    /// Push accumulated progress to the UI. The manager calls this on a timer rather
    /// than per buffer read -- a PropertyChanged per 1 MB read would flood the
    /// dispatcher on a fast connection.
    /// </summary>
    public void FlushProgressToUi() => OnPropertiesChanged(
        nameof(DownloadedBytes), nameof(ProgressPercent), nameof(ProgressText),
        nameof(RemainingBytes), nameof(BytesPerSecond), nameof(SpeedText), nameof(EtaText));

    public void ResetForRetry()
    {
        ErrorMessage = null;
        TechnicalDetails = null;
        Attempt = 0;
        PauseRequested = false;
        _meter.Reset();
        State = TransferState.Queued;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public override string ToString() => RelativePath;
}
