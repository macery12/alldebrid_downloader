using AllDebridDownloader.Configuration;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Services;

namespace AllDebridDownloader.ViewModels;

/// <summary>
/// The Settings tab. Every setter saves immediately -- there is no Apply button.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    /// <summary>Speed cap choices, as (label, bytes per second).</summary>
    public sealed record SpeedOption(string Label, long BytesPerSecond)
    {
        public override string ToString() => Label;
    }

    private readonly ConfigService _config;
    private readonly DownloadManager _downloads;
    private readonly Logger _log;

    public SettingsViewModel(ConfigService config, DownloadManager downloads, Logger log)
    {
        _config = config;
        _downloads = downloads;
        _log = log;

        SpeedOptions =
        [
            new SpeedOption("Unlimited", 0),
            new SpeedOption("1 MB/s", 1L * 1024 * 1024),
            new SpeedOption("5 MB/s", 5L * 1024 * 1024),
            new SpeedOption("10 MB/s", 10L * 1024 * 1024),
            new SpeedOption("25 MB/s", 25L * 1024 * 1024),
            new SpeedOption("50 MB/s", 50L * 1024 * 1024),
            new SpeedOption("100 MB/s", 100L * 1024 * 1024)
        ];

        BrowseFolderCommand = new RelayCommand(BrowseForFolder);
        OpenFolderCommand = new RelayCommand(() => OpenFolder(DownloadDirectory));
        OpenConfigFolderCommand = new RelayCommand(() => OpenFolder(_config.ConfigDirectory));
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(_config.LogDirectory));
    }

    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }

    /// <summary>Set by MainWindow so Change API key / Re-link can reopen the dialog.</summary>
    public Action? ChangeApiKeyRequested { get; set; }
    public Action? SignOutRequested { get; set; }

    public event Action? DownloadDirectoryChanged;

    public IReadOnlyList<SpeedOption> SpeedOptions { get; }

    public IReadOnlyList<int> ConcurrentFileOptions { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    public IReadOnlyList<int> ConnectionOptions { get; } = [1, 2, 4, 6, 8, 12, 16];

    public IReadOnlyList<int> PollIntervalOptions { get; } = [2, 3, 5, 10, 15];

    public IReadOnlyList<ExistingFilePolicy> PolicyOptions { get; } =
        Enum.GetValues<ExistingFilePolicy>();

    public string DownloadDirectory
    {
        get => _config.Current.DownloadDirectory ?? "";
        set
        {
            if (_config.Current.DownloadDirectory == value) return;

            if (!ConfigService.IsUsableDownloadDirectory(value, out var problem))
            {
                FolderProblem = problem;
                return;
            }

            FolderProblem = null;
            _config.Current.DownloadDirectory = Path.GetFullPath(value);
            _config.SaveSoon();
            _log.Info("Config", "Download folder changed.");

            OnPropertyChanged();
            DownloadDirectoryChanged?.Invoke();
        }
    }

    private string? _folderProblem;
    public string? FolderProblem
    {
        get => _folderProblem;
        private set
        {
            if (SetProperty(ref _folderProblem, value)) OnPropertyChanged(nameof(HasFolderProblem));
        }
    }

    public bool HasFolderProblem => !string.IsNullOrEmpty(FolderProblem);

    public int MaxConcurrentFiles
    {
        get => _config.Current.MaxConcurrentFiles;
        set
        {
            if (_config.Current.MaxConcurrentFiles == value) return;
            _config.Current.MaxConcurrentFiles = value;
            Apply();
            OnPropertyChanged();
        }
    }

    public int ConnectionsPerFile
    {
        get => _config.Current.ConnectionsPerFile;
        set
        {
            if (_config.Current.ConnectionsPerFile == value) return;
            _config.Current.ConnectionsPerFile = value;
            Apply();
            OnPropertyChanged();
        }
    }

    public SpeedOption SelectedSpeed
    {
        get => SpeedOptions.FirstOrDefault(
                   o => o.BytesPerSecond == _config.Current.GlobalSpeedLimitBytesPerSecond)
               ?? SpeedOptions[0];
        set
        {
            if (_config.Current.GlobalSpeedLimitBytesPerSecond == value.BytesPerSecond) return;
            _config.Current.GlobalSpeedLimitBytesPerSecond = value.BytesPerSecond;
            Apply();
            OnPropertyChanged();
        }
    }

    public ExistingFilePolicy ExistingFilePolicy
    {
        get => _config.Current.ExistingFilePolicy;
        set
        {
            if (_config.Current.ExistingFilePolicy == value) return;
            _config.Current.ExistingFilePolicy = value;
            _config.SaveSoon();
            OnPropertyChanged();
        }
    }

    public bool ResumeOnStartup
    {
        get => _config.Current.ResumeOnStartup;
        set
        {
            if (_config.Current.ResumeOnStartup == value) return;
            _config.Current.ResumeOnStartup = value;
            _config.SaveSoon();
            OnPropertyChanged();
        }
    }

    public int StatusPollIntervalSeconds
    {
        get => _config.Current.StatusPollIntervalSeconds;
        set
        {
            if (_config.Current.StatusPollIntervalSeconds == value) return;
            _config.Current.StatusPollIntervalSeconds = value;
            _config.SaveSoon();
            OnPropertyChanged();
        }
    }

    public bool DeleteMagnetAfterDownload
    {
        get => _config.Current.DeleteMagnetAfterDownload;
        set
        {
            if (_config.Current.DeleteMagnetAfterDownload == value) return;
            _config.Current.DeleteMagnetAfterDownload = value;
            _config.SaveSoon();
            OnPropertyChanged();
        }
    }

    public string ConfigPath => _config.ConfigPath;
    public string LogDirectory => _config.LogDirectory;

    private void Apply()
    {
        _config.SaveSoon();

        // In-flight transfers keep their settings; new ones pick these up.
        _downloads.ApplySettings();
    }

    private void BrowseForFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the download folder",
            Multiselect = false
        };

        if (Directory.Exists(DownloadDirectory)) dialog.InitialDirectory = DownloadDirectory;

        if (dialog.ShowDialog() == true) DownloadDirectory = dialog.FolderName;
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("Shell", "Could not open " + Redact.Url(path) + ": " + ex.Message);
        }
    }
}
