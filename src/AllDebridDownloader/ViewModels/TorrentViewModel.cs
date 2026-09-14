using AllDebridDownloader.Helpers;
using AllDebridDownloader.Models;

namespace AllDebridDownloader.ViewModels;

/// <summary>One row in the Torrents list, plus its lazily fetched file tree.</summary>
public sealed class TorrentViewModel : ObservableObject
{
    private string _name = "(unnamed)";
    private string _statusText = "Waiting";
    private MagnetStatusKind _kind = MagnetStatusKind.Processing;
    private long _size;
    private long _downloaded;
    private int? _seeders;
    private long _apiDownloadSpeed;
    private FileNodeViewModel? _tree;
    private bool _isLoadingFiles;
    private string? _fileError;
    private bool _filesRequested;

    public TorrentViewModel(long id) => Id = id;

    public long Id { get; }

    /// <summary>Hash from the upload response, used as a folder-name fallback.</summary>
    public string? Hash { get; set; }

    /// <summary>Set when the magnet could not even be added; shows as a failed row.</summary>
    public string? AddError { get; init; }

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(FolderName)); }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public MagnetStatusKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value)) return;
            OnPropertiesChanged(nameof(IsReady), nameof(IsError), nameof(IsProcessing),
                nameof(CanRestart));
        }
    }

    public long Size
    {
        get => _size;
        set
        {
            if (!SetProperty(ref _size, value)) return;
            OnPropertiesChanged(nameof(SizeText), nameof(ProgressPercent), nameof(ProgressText));
        }
    }

    public long Downloaded
    {
        get => _downloaded;
        set
        {
            if (!SetProperty(ref _downloaded, value)) return;
            OnPropertiesChanged(nameof(ProgressPercent), nameof(ProgressText));
        }
    }

    public int? Seeders
    {
        get => _seeders;
        set { if (SetProperty(ref _seeders, value)) OnPropertyChanged(nameof(SeedersText)); }
    }

    public long ApiDownloadSpeed
    {
        get => _apiDownloadSpeed;
        set { if (SetProperty(ref _apiDownloadSpeed, value)) OnPropertyChanged(nameof(SpeedText)); }
    }

    public FileNodeViewModel? Tree
    {
        get => _tree;
        set
        {
            if (!SetProperty(ref _tree, value)) return;
            OnPropertiesChanged(nameof(HasTree), nameof(SelectedFileCount), nameof(SelectedBytes),
                nameof(SelectionSummary));
        }
    }

    public bool IsLoadingFiles
    {
        get => _isLoadingFiles;
        set => SetProperty(ref _isLoadingFiles, value);
    }

    public string? FileError
    {
        get => _fileError;
        set { if (SetProperty(ref _fileError, value)) OnPropertyChanged(nameof(HasFileError)); }
    }

    /// <summary>Set once files have been requested, so a ready magnet is not re-fetched.</summary>
    public bool FilesRequested
    {
        get => _filesRequested;
        set => SetProperty(ref _filesRequested, value);
    }

    // ---- computed --------------------------------------------------------

    public bool IsReady => Kind == MagnetStatusKind.Ready;
    public bool IsError => Kind == MagnetStatusKind.Error || AddError is not null;
    public bool IsProcessing => Kind == MagnetStatusKind.Processing;
    public bool CanRestart => Kind == MagnetStatusKind.Error;

    public bool HasTree => Tree is not null;
    public bool HasFileError => !string.IsNullOrEmpty(FileError);

    public string SizeText => Size > 0 ? ByteFormatter.Format(Size) : "—";

    public double ProgressPercent => Size > 0
        ? Math.Clamp(Downloaded * 100.0 / Size, 0, 100)
        : IsReady ? 100 : 0;

    public string ProgressText => IsReady
        ? "100%"
        : Size > 0 && Downloaded > 0 ? ProgressPercent.ToString("0") + "%" : "—";

    public string SeedersText => Seeders is > 0 ? Seeders.Value.ToString() : "—";

    public string SpeedText => ApiDownloadSpeed > 0
        ? ByteFormatter.FormatRate(ApiDownloadSpeed)
        : "—";

    /// <summary>
    /// The folder this torrent's files go into. Falls back through name, then hash, so a
    /// torrent AllDebrid calls "noname" still lands somewhere sensible.
    /// </summary>
    public string FolderName
    {
        get
        {
            var candidate = Name;
            if (string.IsNullOrWhiteSpace(candidate)
                || candidate.Equals("noname", StringComparison.OrdinalIgnoreCase)
                || candidate == "(unnamed)")
            {
                candidate = !string.IsNullOrWhiteSpace(Hash) ? Hash! : "magnet-" + Id;
            }
            return PathHelper.SanitizeSegment(candidate);
        }
    }

    public int SelectedFileCount => Tree?.SelectedFiles().Count() ?? 0;
    public long SelectedBytes => Tree?.SelectedFiles().Sum(f => f.FileSize) ?? 0;

    public string SelectionSummary => Tree is null
        ? ""
        : "Selected: " + SelectedFileCount + " file" + (SelectedFileCount == 1 ? "" : "s")
          + " · " + ByteFormatter.Format(SelectedBytes);

    public void RaiseSelectionSummary() => OnPropertiesChanged(
        nameof(SelectedFileCount), nameof(SelectedBytes), nameof(SelectionSummary));

    /// <summary>Copy the fields from a poll into this row.</summary>
    public void UpdateFrom(MagnetStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.Filename)) Name = status.Filename!;
        if (!string.IsNullOrWhiteSpace(status.Hash)) Hash = status.Hash;
        if (status.Size is > 0) Size = status.Size.Value;
        if (status.Downloaded is not null) Downloaded = status.Downloaded.Value;
        if (status.Seeders is not null) Seeders = status.Seeders;
        if (status.DownloadSpeed is not null) ApiDownloadSpeed = status.DownloadSpeed.Value;

        Kind = status.Kind;
        StatusText = status.StatusText;
    }
}
