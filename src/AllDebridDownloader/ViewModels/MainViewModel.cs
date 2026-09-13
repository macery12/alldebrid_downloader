using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using AllDebridDownloader.Configuration;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Models;
using AllDebridDownloader.Services;
using AllDebridDownloader.Views;

namespace AllDebridDownloader.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    // A magnet URI, or a bare 40-char hex / 32-char base32 infohash.
    [GeneratedRegex(@"^magnet:\?.*xt=urn:btih:[a-zA-Z0-9]{32,40}", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetUriPattern();

    [GeneratedRegex(@"^([a-fA-F0-9]{40}|[a-zA-Z2-7]{32})$")]
    private static partial Regex BareHashPattern();

    private const long MaxTorrentFileBytes = 10L * 1024 * 1024;

    private readonly Logger _log;
    private readonly ConfigService _config;
    private readonly MagnetPoller _poller;
    private readonly LinkResolver _resolver;

    private string _magnetInput = string.Empty;
    private string _statusMessage = "Starting…";
    private string? _notice;
    private string? _noticeDetails;
    private bool _noticeIsError;
    private TorrentViewModel? _selectedTorrent;
    private UserInfo? _user;
    private bool _isBusy;

    public MainViewModel(Logger log, ConfigService config)
    {
        _log = log;
        _config = config;

        Client = new AllDebridClient(log);
        _resolver = new LinkResolver(Client, log);
        _poller = new MagnetPoller(Client, log, config);
        Downloads = new DownloadManager(log, config, _resolver);
        Settings = new SettingsViewModel(config, Downloads, log);

        Downloads.ConflictHandler = AskConflictAsync;
        Downloads.AggregateChanged += OnAggregateChanged;
        Downloads.TorrentCompleted += OnTorrentCompleted;

        _poller.StatusUpdated += OnStatusUpdated;
        _poller.MagnetBecameReady += OnMagnetReady;
        _poller.ConnectionNotice += OnConnectionNotice;

        Client.RateLimited += wait => SetNotice(
            "AllDebrid is rate limiting this app; waiting "
            + wait.TotalSeconds.ToString("0") + "s.", isError: false);

        Settings.DownloadDirectoryChanged += () => OnPropertyChanged(nameof(DestinationPreview));

        AddMagnetCommand = new AsyncRelayCommand(AddMagnetAsync, () => !IsBusy);
        AddTorrentFileCommand = new AsyncRelayCommand(AddTorrentFileAsync, () => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        DeleteMagnetCommand = new AsyncRelayCommand(DeleteSelectedAsync,
            () => SelectedTorrent is not null);
        RestartMagnetCommand = new AsyncRelayCommand(RestartSelectedAsync,
            () => SelectedTorrent?.CanRestart == true);
        DownloadSelectedCommand = new AsyncRelayCommand(DownloadSelectedAsync,
            () => SelectedTorrent?.SelectedFileCount > 0);

        SelectAllCommand = new RelayCommand(() => ApplyTreeSelection(true));
        SelectNoneCommand = new RelayCommand(() => ApplyTreeSelection(false));
        SelectVideoOnlyCommand = new RelayCommand(() => SelectedTorrent?.Tree?.SelectVideoOnly());

        OpenDestinationCommand = new RelayCommand(OpenDestinationFolder);
        CopyHashCommand = new RelayCommand(CopySelectedHash);
        DismissNoticeCommand = new RelayCommand(() => SetNotice(null));

        PauseAllCommand = new RelayCommand(() => Downloads.PauseAll());
        ResumeAllCommand = new RelayCommand(() => Downloads.ResumeAll());
        CancelAllCommand = new RelayCommand(CancelAllWithConfirm);
        ClearFinishedCommand = new RelayCommand(() => Downloads.ClearFinished());

        PauseCommand = new RelayCommand(p => { if (p is TransferItem t) Downloads.Pause(t); });
        ResumeCommand = new RelayCommand(p => { if (p is TransferItem t) Downloads.Resume(t); });
        CancelCommand = new RelayCommand(p => { if (p is TransferItem t) CancelOne(t); });
        RetryCommand = new RelayCommand(p => { if (p is TransferItem t) Downloads.Resume(t); });
        OpenContainingFolderCommand = new RelayCommand(OpenContainingFolder);
        CopyErrorCommand = new RelayCommand(CopyTransferError);

        foreach (var cmd in new[] { AddMagnetCommand, AddTorrentFileCommand, RefreshCommand,
            DeleteMagnetCommand, RestartMagnetCommand, DownloadSelectedCommand })
        {
            cmd.OnError = ex => ReportError(ex);
        }
    }

    public AllDebridClient Client { get; }
    public DownloadManager Downloads { get; }
    public SettingsViewModel Settings { get; }
    public Logger Log => _log;

    public ObservableCollection<TorrentViewModel> Torrents { get; } = new();

    // ---- commands --------------------------------------------------------

    public AsyncRelayCommand AddMagnetCommand { get; }
    public AsyncRelayCommand AddTorrentFileCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand DeleteMagnetCommand { get; }
    public AsyncRelayCommand RestartMagnetCommand { get; }
    public AsyncRelayCommand DownloadSelectedCommand { get; }

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand SelectVideoOnlyCommand { get; }
    public RelayCommand OpenDestinationCommand { get; }
    public RelayCommand CopyHashCommand { get; }
    public RelayCommand DismissNoticeCommand { get; }

    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ResumeAllCommand { get; }
    public RelayCommand CancelAllCommand { get; }
    public RelayCommand ClearFinishedCommand { get; }

    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand OpenContainingFolderCommand { get; }
    public RelayCommand CopyErrorCommand { get; }

    // ---- bindable state --------------------------------------------------

    public string MagnetInput
    {
        get => _magnetInput;
        set => SetProperty(ref _magnetInput, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string? Notice
    {
        get => _notice;
        private set { if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); }
    }

    public string? NoticeDetails
    {
        get => _noticeDetails;
        private set
        {
            if (SetProperty(ref _noticeDetails, value)) OnPropertyChanged(nameof(HasNoticeDetails));
        }
    }

    public bool NoticeIsError
    {
        get => _noticeIsError;
        private set => SetProperty(ref _noticeIsError, value);
    }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool HasNoticeDetails => !string.IsNullOrEmpty(NoticeDetails);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            AddMagnetCommand.RaiseCanExecuteChanged();
            AddTorrentFileCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public TorrentViewModel? SelectedTorrent
    {
        get => _selectedTorrent;
        set
        {
            if (!SetProperty(ref _selectedTorrent, value)) return;

            OnPropertiesChanged(nameof(HasSelection), nameof(DestinationPreview));
            DeleteMagnetCommand.RaiseCanExecuteChanged();
            RestartMagnetCommand.RaiseCanExecuteChanged();
            DownloadSelectedCommand.RaiseCanExecuteChanged();

            // Fetch the tree the first time a ready torrent is looked at.
            if (value is { IsReady: true, HasTree: false, FilesRequested: false })
                _ = LoadFilesAsync(value);
        }
    }

    public bool HasSelection => SelectedTorrent is not null;

    public UserInfo? User
    {
        get => _user;
        private set
        {
            if (!SetProperty(ref _user, value)) return;
            OnPropertyChanged(nameof(AccountText));
        }
    }

    public string AccountText => User is null
        ? "Not connected"
        : "Connected — " + (User.Username ?? "(unknown)") + " (" + User.AccountSummary + ")";

    /// <summary>Where the selected torrent's files will land, shown before committing.</summary>
    public string DestinationPreview
    {
        get
        {
            var root = _config.Current.DownloadDirectory;
            if (string.IsNullOrEmpty(root) || SelectedTorrent is null) return "";
            return Path.Combine(root, SelectedTorrent.FolderName) + Path.DirectorySeparatorChar;
        }
    }

    // ---- aggregate progress, bound by the Downloads tab -------------------

    public string AggregateProgressText =>
        ByteFormatter.Format(Downloads.DownloadedBytes) + " / "
        + ByteFormatter.Format(Downloads.TotalBytes);

    public double AggregatePercent => Downloads.AggregatePercent;
    public string AggregateSpeedText => ByteFormatter.FormatRate(Downloads.AggregateBytesPerSecond);
    public string AggregateEtaText => TimeFormatter.FormatEta(Downloads.AggregateEta);

    public string ActivityText
    {
        get
        {
            var active = Downloads.ActiveCount;
            var queued = Downloads.QueuedCount;
            if (active == 0 && queued == 0) return "Idle";
            var parts = new List<string>();
            if (active > 0) parts.Add(active + " active");
            if (queued > 0) parts.Add(queued + " queued");
            return string.Join(" · ", parts) + " · " + AggregateSpeedText;
        }
    }

    private void OnAggregateChanged() => OnPropertiesChanged(
        nameof(AggregateProgressText), nameof(AggregatePercent), nameof(AggregateSpeedText),
        nameof(AggregateEtaText), nameof(ActivityText));

    public void RaiseDownloadDirectoryChanged() =>
        OnPropertiesChanged(nameof(DestinationPreview));

    // -----------------------------------------------------------------------
    // Startup
    // -----------------------------------------------------------------------

    /// <summary>Try a saved key without user interaction. False means it is no longer valid.</summary>
    public bool TryUseSavedKey(string apiKey)
    {
        try
        {
            Client.ApiKey = apiKey;
            var user = Client.GetUserAsync(CancellationToken.None).GetAwaiter().GetResult();
            User = user;
            _log.Info("Auth", "Reconnected as " + (user.Username ?? "(unknown)")
                + " (" + user.AccountSummary + ").");
            return true;
        }
        catch (AllDebridApiException ex)
        {
            _log.Warn("Auth", "Saved key rejected: " + (ex.Code ?? "(no code)"));
            Client.ApiKey = null;
            return false;
        }
        catch (Exception ex)
        {
            // Offline at launch is not a reason to throw away a working key.
            _log.Warn("Auth", "Could not verify the saved key: " + ex.Message);
            User = null;
            StatusMessage = "Offline — could not reach AllDebrid.";
            return true;
        }
    }

    public void ApplyConnectedKey(string apiKey, UserInfo? user)
    {
        Client.ApiKey = apiKey;
        User = user;
        _config.Current.ApiKey = apiKey;
        _config.SaveSoon();
    }

    /// <summary>Forget the saved key. Local downloads are untouched.</summary>
    public void SignOut()
    {
        Client.ApiKey = null;
        User = null;
        _config.Current.ApiKey = null;
        _config.SaveSoon();
        StatusMessage = "Signed out.";
        _log.Info("Auth", "Signed out; the saved API key was removed.");
    }

    public async Task StartAsync()
    {
        StatusMessage = AccountText;
        _poller.Start();

        // Offer to pick up anything an earlier session left half-finished.
        if (_config.Current.ResumeOnStartup) await OfferResumeAsync();
    }

    private async Task OfferResumeAsync()
    {
        var root = _config.Current.DownloadDirectory;
        if (string.IsNullOrEmpty(root)) return;

        var interrupted = await Task.Run(() => DownloadManager.FindInterrupted(root, _log));
        if (interrupted.Count == 0) return;

        _log.Info("Download", "Found " + interrupted.Count + " interrupted download(s).");

        var dialog = new ResumePromptDialog(interrupted, root)
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();

        switch (dialog.Decision)
        {
            case ResumeDecision.Resume:
                var items = interrupted
                    .Select(i => DownloadManager.ToTransfer(i.FinalPath, i.Sidecar, root))
                    .Where(t => t is not null)
                    .Select(t => t!)
                    .ToList();

                var skipped = interrupted.Count - items.Count;
                if (skipped > 0)
                {
                    SetNotice(skipped + " interrupted file(s) could not be resumed because their "
                        + "resume state was incomplete; they will restart if downloaded again.",
                        isError: false);
                }

                await Downloads.EnqueueAsync(items);
                break;

            case ResumeDecision.Discard:
                foreach (var (finalPath, _) in interrupted)
                {
                    try
                    {
                        var part = SidecarStore.PartPathFor(finalPath);
                        if (File.Exists(part)) File.Delete(part);
                        SidecarStore.Delete(finalPath);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Download", "Could not discard "
                            + Path.GetFileName(finalPath) + ": " + ex.Message);
                    }
                }
                _log.Info("Download", "Discarded " + interrupted.Count + " partial download(s).");
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Adding magnets and torrents
    // -----------------------------------------------------------------------

    private async Task AddMagnetAsync()
    {
        var raw = MagnetInput.Trim();
        if (raw.Length == 0) return;

        // Accept several magnets pasted as separate lines, in one API call.
        var candidates = raw
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var valid = new List<string>();
        var invalid = new List<string>();

        foreach (var candidate in candidates)
        {
            if (LooksLikeMagnet(candidate)) valid.Add(candidate);
            else invalid.Add(candidate);
        }

        if (valid.Count == 0)
        {
            SetNotice("That doesn't look like a magnet link or an info hash.", isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            var results = await Client.UploadMagnetsAsync(valid);
            HandleUploadResults(results, "magnet");

            // Only clear the box when everything in it was accepted.
            if (invalid.Count == 0 && results.All(r => !r.Failed)) MagnetInput = string.Empty;
            else if (invalid.Count > 0) MagnetInput = string.Join(Environment.NewLine, invalid);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static bool LooksLikeMagnet(string value)
    {
        value = value.Trim();
        return MagnetUriPattern().IsMatch(value) || BareHashPattern().IsMatch(value);
    }

    private async Task AddTorrentFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add .torrent files",
            Filter = "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*",
            Multiselect = true
        };

        var last = _config.Current.LastTorrentPickerDirectory;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) dialog.InitialDirectory = last;

        if (dialog.ShowDialog() != true) return;

        _config.Current.LastTorrentPickerDirectory = Path.GetDirectoryName(dialog.FileNames[0]);
        _config.SaveSoon();

        await AddTorrentFilesAsync(dialog.FileNames);
    }

    /// <summary>Shared by the file picker and by drag-and-drop.</summary>
    public async Task AddTorrentFilesAsync(IEnumerable<string> paths)
    {
        var accepted = new List<string>();
        var rejected = new List<string>();

        foreach (var path in paths)
        {
            if (!path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                if (new FileInfo(path).Length > MaxTorrentFileBytes)
                {
                    rejected.Add(Path.GetFileName(path) + " (larger than 10 MB)");
                    continue;
                }
                accepted.Add(path);
            }
            catch (Exception ex)
            {
                rejected.Add(Path.GetFileName(path) + " (" + ex.Message + ")");
            }
        }

        if (rejected.Count > 0)
            SetNotice("Skipped: " + string.Join(", ", rejected), isError: true);

        if (accepted.Count == 0) return;

        IsBusy = true;
        try
        {
            var results = await Client.UploadTorrentFilesAsync(accepted);
            HandleUploadResults(results, "torrent");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Add a row per uploaded item. Per-item errors are shown as failed rows rather than
    /// discarding the whole response.
    /// </summary>
    private void HandleUploadResults(List<UploadedMagnet> results, string kind)
    {
        var added = 0;
        var failed = new List<string>();

        foreach (var result in results)
        {
            if (result.Failed)
            {
                var message = ApiErrorMessages.Friendly(result.Error?.Code, result.Error?.Message);
                failed.Add(message);

                Torrents.Insert(0, new TorrentViewModel(0)
                {
                    Name = Shorten(result.SourceLabel, 60),
                    StatusText = message,
                    AddError = message,
                    Kind = MagnetStatusKind.Error
                });
                continue;
            }

            var existing = Torrents.FirstOrDefault(t => t.Id == result.Id);
            if (existing is not null)
            {
                existing.Hash = result.Hash;
                continue;
            }

            var vm = new TorrentViewModel(result.Id)
            {
                Name = string.IsNullOrWhiteSpace(result.Name) ? "(unnamed)" : result.Name!,
                Hash = result.Hash,
                Size = result.Size,
                Kind = result.Ready ? MagnetStatusKind.Ready : MagnetStatusKind.Processing,
                StatusText = result.Ready ? "Ready" : "In queue"
            };

            Torrents.Insert(0, vm);
            _poller.Track(result);
            added++;

            SelectedTorrent ??= vm;
        }

        if (added > 0)
        {
            _log.Info("Magnet", "Added " + added + " " + kind + "(s).");
            SetNotice(null);
        }

        if (failed.Count > 0)
            SetNotice(failed.Count == 1 ? failed[0] : failed.Count + " items failed to add.", true);
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    // -----------------------------------------------------------------------
    // Status updates
    // -----------------------------------------------------------------------

    private void OnStatusUpdated(IReadOnlyList<MagnetStatus> statuses)
    {
        OnUi(() =>
        {
            foreach (var status in statuses)
            {
                var vm = Torrents.FirstOrDefault(t => t.Id == status.Id);
                if (vm is null)
                {
                    // A magnet added elsewhere (the website, another session).
                    vm = new TorrentViewModel(status.Id);
                    vm.UpdateFrom(status);
                    Torrents.Add(vm);
                }
                else
                {
                    vm.UpdateFrom(status);
                }
            }

            RestartMagnetCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(DestinationPreview));
        });
    }

    private void OnMagnetReady(MagnetStatus status)
    {
        OnUi(() =>
        {
            var vm = Torrents.FirstOrDefault(t => t.Id == status.Id);
            if (vm is null) return;

            // Fetch files for whatever the user is looking at; otherwise wait until they
            // select it, so a big account does not trigger dozens of calls at once.
            if (!vm.FilesRequested && (ReferenceEquals(vm, SelectedTorrent) || Torrents.Count <= 5))
                _ = LoadFilesAsync(vm);
        });
    }

    private void OnConnectionNotice(string? message) => OnUi(() =>
    {
        if (message is null)
        {
            if (!NoticeIsError) SetNotice(null);
            StatusMessage = AccountText;
        }
        else
        {
            StatusMessage = message;
        }
    });

    // -----------------------------------------------------------------------
    // File tree
    // -----------------------------------------------------------------------

    private async Task LoadFilesAsync(TorrentViewModel torrent)
    {
        if (torrent.Id == 0 || torrent.IsLoadingFiles) return;

        torrent.FilesRequested = true;
        torrent.IsLoadingFiles = true;
        torrent.FileError = null;

        try
        {
            var entries = await Client.GetFilesAsync([torrent.Id]);
            var entry = entries.FirstOrDefault(e => e.Id == torrent.Id) ?? entries.FirstOrDefault();

            if (entry?.Error is not null)
            {
                torrent.FileError = ApiErrorMessages.Friendly(entry.Error.Code, entry.Error.Message);
                return;
            }

            if (entry?.Files is null || entry.Files.Count == 0)
            {
                torrent.FileError = "AllDebrid returned no files for this torrent.";
                return;
            }

            var tree = FileNodeViewModel.BuildTree(torrent.Name, entry.Files);
            tree.SelectionChanged += () =>
            {
                torrent.RaiseSelectionSummary();
                DownloadSelectedCommand.RaiseCanExecuteChanged();
            };

            torrent.Tree = tree;
            torrent.RaiseSelectionSummary();
            DownloadSelectedCommand.RaiseCanExecuteChanged();

            _log.Info("Files", "Loaded " + tree.AllFiles().Count() + " file(s) for magnet "
                + torrent.Id + ".");
        }
        catch (AllDebridApiException ex)
        {
            torrent.FileError = ex.Message;
            torrent.FilesRequested = false;   // allow a retry via Refresh
            _log.Warn("Files", "Could not load files for magnet " + torrent.Id + ": " + ex.Message);
        }
        catch (Exception ex)
        {
            torrent.FileError = "Could not load the file list: " + ex.Message;
            torrent.FilesRequested = false;
            _log.Error("Files", "Unexpected failure loading files.", ex);
        }
        finally
        {
            torrent.IsLoadingFiles = false;
        }
    }

    private void ApplyTreeSelection(bool selected)
    {
        var tree = SelectedTorrent?.Tree;
        if (tree is null) return;

        tree.SetSelectedRecursive(selected);
        SelectedTorrent!.RaiseSelectionSummary();
        DownloadSelectedCommand.RaiseCanExecuteChanged();
    }

    // -----------------------------------------------------------------------
    // Magnet management
    // -----------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await _poller.RefreshNowAsync();

            // Re-fetch the open tree too, so Refresh also recovers a failed file list.
            if (SelectedTorrent is { IsReady: true } selected)
            {
                selected.FilesRequested = false;
                selected.Tree = null;
                await LoadFilesAsync(selected);
            }

            SetNotice(null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var torrent = SelectedTorrent;
        if (torrent is null) return;

        if (torrent.Id == 0)
        {
            // A row for an add that never produced a magnet: just drop it locally.
            Torrents.Remove(torrent);
            SelectedTorrent = Torrents.FirstOrDefault();
            return;
        }

        var confirm = MessageBox.Show(
            "Remove \"" + torrent.Name + "\" from your AllDebrid account?\n\n"
            + "Files already downloaded to this PC are not affected.",
            "Delete magnet",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        await Client.DeleteMagnetAsync(torrent.Id);
        _poller.Forget(torrent.Id);
        Torrents.Remove(torrent);
        SelectedTorrent = Torrents.FirstOrDefault();
        SetNotice(null);
    }

    private async Task RestartSelectedAsync()
    {
        var torrent = SelectedTorrent;
        if (torrent is null || torrent.Id == 0) return;

        try
        {
            await Client.RestartMagnetAsync(torrent.Id);
            torrent.StatusText = "Restarting…";
            torrent.Kind = MagnetStatusKind.Processing;
            torrent.FilesRequested = false;
            torrent.Tree = null;
            _poller.RequestImmediatePoll();
        }
        catch (AllDebridApiException ex) when (ex.Code == "MAGNET_PROCESSING")
        {
            // The row was stale; a refresh will show the real state.
            SetNotice("That torrent is already processing or finished.", isError: false);
            await _poller.RefreshNowAsync();
        }
    }

    private void CopySelectedHash()
    {
        var hash = SelectedTorrent?.Hash;
        if (string.IsNullOrEmpty(hash)) return;

        try { Clipboard.SetText(hash); SetNotice("Hash copied.", isError: false); }
        catch { SetNotice("Windows would not let the app use the clipboard.", isError: true); }
    }

    // -----------------------------------------------------------------------
    // Downloading
    // -----------------------------------------------------------------------

    private async Task DownloadSelectedAsync()
    {
        var torrent = SelectedTorrent;
        var tree = torrent?.Tree;
        if (torrent is null || tree is null) return;

        var root = _config.Current.DownloadDirectory;
        if (string.IsNullOrEmpty(root))
        {
            SetNotice("No download folder is set. Choose one in Settings.", isError: true);
            return;
        }

        var selected = tree.SelectedFiles().ToList();
        if (selected.Count == 0) return;

        // Every torrent gets its own folder, single-file ones included.
        var torrentFolder = Path.Combine(root, torrent.FolderName);

        var items = new List<TransferItem>();
        var refused = new List<string>();
        var tooLong = new List<string>();

        foreach (var file in selected)
        {
            var segments = file.PathSegments();

            // The tree root is named after the torrent; if the API also wraps everything
            // in a folder of the same name, don't nest it twice.
            if (segments.Count > 1
                && string.Equals(PathHelper.SanitizeSegment(segments[0]), torrent.FolderName,
                    StringComparison.OrdinalIgnoreCase))
            {
                segments.RemoveAt(0);
            }

            string finalPath;
            try
            {
                finalPath = PathHelper.CombineSafe(torrentFolder, segments);

                var shortened = PathHelper.ShortenIfTooLong(finalPath);
                if (shortened is not null)
                {
                    _log.Warn("Path", "Shortened an over-long path for " + file.Name + ".");
                    finalPath = shortened;
                }
            }
            catch (UnsafePathException ex)
            {
                _log.Error("Path", "Refused unsafe path from the API: " + ex.RelativePath);
                refused.Add(file.Name);
                continue;
            }
            catch (PathTooLongException ex)
            {
                _log.Error("Path", "Refused an unplaceable path: " + ex.Message);
                tooLong.Add(file.Name);
                continue;
            }

            items.Add(new TransferItem
            {
                SourceLink = file.Link!,
                FinalPath = finalPath,
                FileName = Path.GetFileName(finalPath),
                RelativePath = Path.GetRelativePath(root, finalPath),
                MagnetId = torrent.Id,
                TorrentName = torrent.FolderName,
                ExpectedSize = file.FileSize
            });
        }

        if (refused.Count > 0)
        {
            SetNotice(refused.Count + " file(s) were refused because their names would have "
                + "written outside the download folder: " + string.Join(", ", refused),
                isError: true);
        }

        if (tooLong.Count > 0)
        {
            SetNotice(tooLong.Count + " file(s) could not be placed because the destination "
                + "path would be too long. Choose a shorter download folder.", isError: true);
        }

        if (items.Count == 0) return;

        // Check free space before starting rather than failing mid-batch.
        var needed = items.Sum(i => i.ExpectedSize);
        var free = DownloadManager.GetFreeSpace(root);
        if (free is not null && needed > free.Value)
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(root));
            SetNotice("Not enough free space on " + drive + " — need "
                + ByteFormatter.Format(needed) + ", "
                + ByteFormatter.Format(free.Value) + " available.", isError: true);
            return;
        }

        Directory.CreateDirectory(torrentFolder);

        var queued = await Downloads.EnqueueAsync(items);

        StatusMessage = queued > 0
            ? "Queued " + queued + " file(s) to " + torrentFolder
            : "Nothing was queued.";
    }

    private async Task<ConflictChoice> AskConflictAsync(ConflictRequest request)
    {
        var tcs = new TaskCompletionSource<ConflictChoice>();

        OnUi(() =>
        {
            var dialog = new FileConflictDialog(request)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.ShowDialog();

            if (dialog.ApplyToAllRemaining) Downloads.MarkApplyToAll(request.FinalPath);
            tcs.SetResult(dialog.Choice);
        });

        return await tcs.Task;
    }

    private void CancelOne(TransferItem item)
    {
        var answer = MessageBox.Show(
            "Delete the partly downloaded file as well?\n\n" + item.FileName,
            "Cancel download",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer == MessageBoxResult.Cancel) return;

        Downloads.Cancel(item, deletePartial: answer == MessageBoxResult.Yes);
    }

    private void CancelAllWithConfirm()
    {
        if (Downloads.Transfers.All(t => t.IsFinished)) return;

        var answer = MessageBox.Show(
            "Cancel all unfinished downloads?\n\nCompleted files are kept either way.",
            "Cancel all",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        Downloads.CancelAll(deletePartials: false);
    }

    private void OnTorrentCompleted(long magnetId)
    {
        var torrent = Torrents.FirstOrDefault(t => t.Id == magnetId);
        _log.Info("Download", "All selected files finished for magnet " + magnetId + ".");

        if (!_config.Current.DeleteMagnetAfterDownload) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Client.DeleteMagnetAsync(magnetId);
                _poller.Forget(magnetId);
                OnUi(() =>
                {
                    if (torrent is not null) Torrents.Remove(torrent);
                });
                _log.Info("Magnet", "Auto-deleted magnet " + magnetId + " after download.");
            }
            catch (Exception ex)
            {
                _log.Warn("Magnet", "Could not auto-delete magnet " + magnetId + ": " + ex.Message);
            }
        });
    }

    // -----------------------------------------------------------------------
    // Shell helpers
    // -----------------------------------------------------------------------

    private void OpenDestinationFolder()
    {
        var path = DestinationPreview;
        if (string.IsNullOrEmpty(path)) path = _config.Current.DownloadDirectory ?? "";
        OpenInExplorer(path);
    }

    private void OpenContainingFolder(object? parameter)
    {
        if (parameter is not TransferItem item) return;

        var dir = Path.GetDirectoryName(item.FinalPath);
        if (string.IsNullOrEmpty(dir)) return;

        // Select the file if it finished; otherwise just open the folder.
        if (File.Exists(item.FinalPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe",
                    "/select,\"" + item.FinalPath + "\"") { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("Shell", "Could not open Explorer: " + ex.Message);
            }
        }

        OpenInExplorer(dir);
    }

    public void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetNotice("Could not open that folder: " + ex.Message, isError: true);
        }
    }

    private void CopyTransferError(object? parameter)
    {
        if (parameter is not TransferItem item) return;

        var text = (item.ErrorMessage ?? "(no error)") + Environment.NewLine
            + (item.TechnicalDetails ?? "");

        try { Clipboard.SetText(text); SetNotice("Error copied.", isError: false); }
        catch { /* clipboard unavailable */ }
    }

    // -----------------------------------------------------------------------
    // Notices
    // -----------------------------------------------------------------------

    private void SetNotice(string? message, bool isError = false, string? details = null)
    {
        OnUi(() =>
        {
            Notice = message;
            NoticeDetails = details;
            NoticeIsError = isError;
        });
    }

    private void ReportError(Exception ex)
    {
        if (ex is AllDebridApiException api)
        {
            SetNotice(api.Message, isError: true, details: api.TechnicalDetails);

            if (api.IsAuthFailure) StatusMessage = "Not connected — " + api.Message;
            return;
        }

        _log.Error("App", "Command failed.", ex);
        SetNotice("Something went wrong: " + ex.Message, isError: true,
            details: ex.GetType().Name);
    }

    /// <summary>Pause transfers and flush state; called as the window closes.</summary>
    public async Task ShutdownAsync()
    {
        await _poller.StopAsync();
        await Downloads.ShutdownAsync();
        await _config.SaveAsync();
    }

    public bool HasActiveDownloads => Downloads.Transfers.Any(t => t.IsActive);

    public void Dispose()
    {
        _poller.Dispose();
        Downloads.Dispose();
        Client.Dispose();
    }
}
