using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using AllDebridDownloader.Configuration;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Models;
using AllDebridDownloader.ViewModels;

namespace AllDebridDownloader.Services;

/// <summary>What the user chose when a file already existed on disk.</summary>
public enum ConflictChoice { Overwrite, Skip, Rename, Cancel }

public sealed class ConflictRequest
{
    public required string FinalPath { get; init; }
    public required string FileName { get; init; }
    public long ExistingSize { get; init; }
    public DateTime ExistingModified { get; init; }
    public long IncomingSize { get; init; }
}

/// <summary>
/// Owns the transfer queue: admission control, the per-file workers, aggregate progress,
/// and the pause/resume/cancel surface the UI drives.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly HttpClient _http;
    private readonly SocketsHttpHandler _handler;
    private readonly Logger _log;
    private readonly ConfigService _config;
    private readonly LinkResolver _resolver;
    private readonly SpeedLimiter _limiter;
    private readonly SegmentedFileDownloader _downloader;
    private readonly DispatcherTimer _uiTimer;

    private readonly SemaphoreSlim _slots;
    private readonly object _queueSync = new();
    private readonly List<TransferItem> _running = new();

    private CancellationTokenSource _globalCts = new();
    private int _configuredSlots;

    public DownloadManager(Logger log, ConfigService config, LinkResolver resolver)
    {
        _log = log;
        _config = config;
        _resolver = resolver;

        var cfg = config.Current;
        _configuredSlots = cfg.MaxConcurrentFiles;
        _slots = new SemaphoreSlim(cfg.MaxConcurrentFiles, 8);
        _limiter = new SpeedLimiter(cfg.GlobalSpeedLimitBytesPerSecond);

        _handler = new SocketsHttpHandler
        {
            // Headroom above files x connections so a retry never waits on the pool.
            MaxConnectionsPerServer = Math.Max(32, cfg.MaxConcurrentFiles * cfg.ConnectionsPerFile * 2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(20),

            // These are already-compressed media files, and decompression would break
            // the byte-range accounting the whole engine depends on.
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };

        _http = new HttpClient(_handler)
        {
            // No client-wide timeout: a 40 GB file is not a stuck request. Timeouts are
            // enforced per operation, and the engine has its own stall detector.
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AllDebridDownloader/1.0");

        _downloader = new SegmentedFileDownloader(_http, log, _limiter);

        // One coalesced UI refresh for every active transfer, ~4 Hz.
        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _uiTimer.Tick += (_, _) => FlushProgress();
        _uiTimer.Start();
    }

    /// <summary>Every transfer this session, finished ones included until cleared.</summary>
    public ObservableCollection<TransferItem> Transfers { get; } = new();

    /// <summary>Asked when a destination file already exists. Set by the main view model.</summary>
    public Func<ConflictRequest, Task<ConflictChoice>>? ConflictHandler { get; set; }

    /// <summary>Raised when a torrent's selected files have all finished successfully.</summary>
    public event Action<long>? TorrentCompleted;

    public event Action? AggregateChanged;

    public bool IsPausedGlobally { get; private set; }

    // -----------------------------------------------------------------------
    // Aggregate figures for the Downloads tab header
    // -----------------------------------------------------------------------

    public long TotalBytes => Transfers.Where(t => t.State != TransferState.Skipped)
        .Sum(t => t.TotalBytes > 0 ? t.TotalBytes : t.ExpectedSize);

    public long DownloadedBytes => Transfers.Sum(t => t.DownloadedBytes);

    public double AggregateBytesPerSecond => Transfers.Where(t => t.IsActive).Sum(t => t.BytesPerSecond);

    public int ActiveCount => Transfers.Count(t => t.IsActive);
    public int QueuedCount => Transfers.Count(t => t.State == TransferState.Queued);

    public double AggregatePercent
    {
        get
        {
            var total = TotalBytes;
            return total > 0 ? Math.Clamp(DownloadedBytes * 100.0 / total, 0, 100) : 0;
        }
    }

    public TimeSpan? AggregateEta => TimeFormatter.EstimateEta(
        Math.Max(0, TotalBytes - DownloadedBytes), AggregateBytesPerSecond);

    private void FlushProgress()
    {
        var any = false;
        foreach (var t in Transfers)
        {
            if (!t.IsActive) continue;
            t.FlushProgressToUi();
            any = true;
        }
        if (any || _dirtyAggregate)
        {
            _dirtyAggregate = false;
            AggregateChanged?.Invoke();
        }
    }

    private bool _dirtyAggregate;

    // -----------------------------------------------------------------------
    // Settings changes applied live
    // -----------------------------------------------------------------------

    public void ApplySettings()
    {
        var cfg = _config.Current;

        _limiter.BytesPerSecond = cfg.GlobalSpeedLimitBytesPerSecond;

        // Grow or shrink the admission semaphore to match the new file limit.
        var delta = cfg.MaxConcurrentFiles - _configuredSlots;
        if (delta > 0) _slots.Release(delta);
        else if (delta < 0) _ = Task.Run(async () =>
        {
            for (var i = 0; i < -delta; i++)
            {
                try { await _slots.WaitAsync(_globalCts.Token).ConfigureAwait(false); }
                catch { return; }
            }
        });

        _configuredSlots = cfg.MaxConcurrentFiles;
    }

    // -----------------------------------------------------------------------
    // Enqueue
    // -----------------------------------------------------------------------

    /// <summary>
    /// Queue a batch of files. Existing-file conflicts are resolved up front, before
    /// any transfer starts, so the user is not interrupted mid-download.
    /// </summary>
    public async Task<int> EnqueueAsync(IEnumerable<TransferItem> items, CancellationToken ct = default)
    {
        var batch = items.ToList();
        if (batch.Count == 0) return 0;

        var accepted = new List<TransferItem>();
        ConflictChoice? applyToAll = null;

        foreach (var item in batch)
        {
            ct.ThrowIfCancellationRequested();

            var resolved = item;

            if (File.Exists(item.FinalPath))
            {
                var choice = applyToAll;

                if (choice is null)
                {
                    var policy = _config.Current.ExistingFilePolicy;
                    if (policy != ExistingFilePolicy.Ask)
                    {
                        choice = policy switch
                        {
                            ExistingFilePolicy.Overwrite => ConflictChoice.Overwrite,
                            ExistingFilePolicy.Skip => ConflictChoice.Skip,
                            ExistingFilePolicy.Rename => ConflictChoice.Rename,
                            _ => ConflictChoice.Skip
                        };
                    }
                    else if (ConflictHandler is not null)
                    {
                        var info = new FileInfo(item.FinalPath);
                        var (decision, forAll) = await AskAsync(new ConflictRequest
                        {
                            FinalPath = item.FinalPath,
                            FileName = item.FileName,
                            ExistingSize = info.Length,
                            ExistingModified = info.LastWriteTime,
                            IncomingSize = item.ExpectedSize
                        }).ConfigureAwait(false);

                        choice = decision;
                        if (forAll) applyToAll = decision;
                    }
                    else
                    {
                        choice = ConflictChoice.Skip;
                    }
                }

                switch (choice)
                {
                    case ConflictChoice.Cancel:
                        _log.Info("Download", "Batch cancelled at the conflict prompt.");
                        return accepted.Count;

                    case ConflictChoice.Skip:
                        _log.Info("Download", "Skipped existing file " + item.FileName + ".");
                        continue;

                    case ConflictChoice.Rename:
                        var renamed = PathHelper.NextAvailableName(item.FinalPath);
                        resolved = CloneWithPath(item, renamed);
                        _log.Info("Download", "Renaming to " + Path.GetFileName(renamed) + ".");
                        break;

                    case ConflictChoice.Overwrite:
                        TryDelete(item.FinalPath);
                        SidecarStore.Delete(item.FinalPath);
                        TryDelete(SidecarStore.PartPathFor(item.FinalPath));
                        break;
                }
            }

            // Skip anything already queued or running for the same destination.
            lock (_queueSync)
            {
                if (Transfers.Any(t => !t.IsFinished
                    && string.Equals(t.FinalPath, resolved.FinalPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }

            accepted.Add(resolved);
        }

        foreach (var item in accepted)
        {
            Transfers.Add(item);
            StartWorker(item);
        }

        _dirtyAggregate = true;
        _log.Info("Download", "Queued " + accepted.Count + " file(s) ("
            + ByteFormatter.Format(accepted.Sum(a => a.ExpectedSize)) + ").");

        return accepted.Count;
    }

    private readonly Dictionary<string, bool> _applyAllFlag = new();

    private async Task<(ConflictChoice Choice, bool ForAll)> AskAsync(ConflictRequest request)
    {
        var handler = ConflictHandler!;
        _applyAllFlag.Remove(request.FinalPath);
        var choice = await handler(request).ConfigureAwait(false);
        var forAll = _applyAllFlag.TryGetValue(request.FinalPath, out var v) && v;
        return (choice, forAll);
    }

    /// <summary>Called by the conflict dialog to flag "do this for all remaining".</summary>
    public void MarkApplyToAll(string finalPath) => _applyAllFlag[finalPath] = true;

    private static TransferItem CloneWithPath(TransferItem source, string newPath) => new()
    {
        SourceLink = source.SourceLink,
        FinalPath = newPath,
        FileName = Path.GetFileName(newPath),
        RelativePath = Path.Combine(
            Path.GetDirectoryName(source.RelativePath) ?? string.Empty,
            Path.GetFileName(newPath)),
        MagnetId = source.MagnetId,
        TorrentName = source.TorrentName,
        ExpectedSize = source.ExpectedSize
    };

    // -----------------------------------------------------------------------
    // Worker
    // -----------------------------------------------------------------------

    private void StartWorker(TransferItem item)
    {
        _ = Task.Run(() => RunAsync(item));
    }

    private async Task RunAsync(TransferItem item)
    {
        var globalToken = _globalCts.Token;

        try
        {
            await _slots.WaitAsync(globalToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            OnUi(() => item.State = TransferState.Cancelled);
            return;
        }

        var itemCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
        item.Cancellation = itemCts;

        lock (_queueSync) _running.Add(item);

        try
        {
            if (item.State is TransferState.Cancelled or TransferState.Skipped) return;

            OnUi(() => item.State = TransferState.Resolving);

            var sidecar = LoadOrCreateSidecar(item);
            OnUi(() =>
            {
                if (sidecar.TotalSize > 0) item.TotalBytes = sidecar.TotalSize;
                item.DownloadedBytes = sidecar.DownloadedBytes;
            });

            void OnBytes(long n) => item.ReportBytes(n);
            _downloader.BytesReceived += OnBytes;

            DownloadResult result;
            try
            {
                OnUi(() =>
                {
                    item.State = TransferState.Downloading;
                    item.ActiveConnections = sidecar.Segments.Count(s => !s.IsComplete);
                });

                result = await _downloader.DownloadAsync(
                    item.PartPath,
                    item.FinalPath,
                    sidecar,
                    (refresh, ct) => _resolver.ResolveAsync(item.SourceLink, refresh, ct),
                    _config.Current.ConnectionsPerFile,
                    itemCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _downloader.BytesReceived -= OnBytes;
            }

            OnUi(() =>
            {
                item.TotalBytes = sidecar.TotalSize;
                item.DownloadedBytes = sidecar.DownloadedBytes;
                item.ActiveConnections = 0;
            });

            switch (result.Outcome)
            {
                case DownloadOutcome.Completed:
                    Finalize(item);
                    break;

                case DownloadOutcome.Cancelled when item.PauseRequested:
                    OnUi(() => item.State = TransferState.Paused);
                    _log.Info("Download", "Paused " + item.FileName + ".");
                    break;

                case DownloadOutcome.Cancelled:
                    OnUi(() => item.State = TransferState.Cancelled);
                    _log.Info("Download", "Cancelled " + item.FileName + ".");
                    break;

                default:
                    OnUi(() =>
                    {
                        item.ErrorMessage = result.ErrorMessage;
                        item.TechnicalDetails = result.TechnicalDetails;
                        item.State = TransferState.Failed;
                    });
                    _log.Error("Download", item.FileName + " failed: " + result.ErrorMessage);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            OnUi(() => item.State = item.PauseRequested
                ? TransferState.Paused
                : TransferState.Cancelled);
        }
        catch (Exception ex)
        {
            _log.Error("Download", "Unexpected failure on " + item.FileName + ".", ex);
            OnUi(() =>
            {
                item.ErrorMessage = "Unexpected error: " + ex.Message;
                item.TechnicalDetails = ex.GetType().Name;
                item.State = TransferState.Failed;
            });
        }
        finally
        {
            lock (_queueSync) _running.Remove(item);
            item.Cancellation = null;
            itemCts.Dispose();
            _slots.Release();
            _dirtyAggregate = true;
            CheckTorrentCompletion(item.MagnetId);
        }
    }

    private PartSidecar LoadOrCreateSidecar(TransferItem item)
    {
        var existing = SidecarStore.TryLoad(item.FinalPath);

        if (existing is not null && File.Exists(item.PartPath))
        {
            _log.Info("Download", "Resuming " + item.FileName + " from "
                + ByteFormatter.Format(existing.DownloadedBytes) + ".");
            existing.SourceLink = item.SourceLink;   // links rotate between sessions
            return existing;
        }

        // A .part with no usable sidecar cannot be trusted; start it over.
        if (existing is null && File.Exists(item.PartPath))
        {
            _log.Warn("Download", "Found " + item.FileName
                + ".part with no usable resume state; starting it over.");
            TryDelete(item.PartPath);
        }

        SidecarStore.Delete(item.FinalPath);

        return new PartSidecar
        {
            SourceLink = item.SourceLink,
            FinalFileName = item.FileName,
            TorrentName = item.TorrentName,
            MagnetId = item.MagnetId,
            TotalSize = item.ExpectedSize
        };
    }

    /// <summary>Rename .part to the real name -- only ever after a verified download.</summary>
    private void Finalize(TransferItem item)
    {
        try
        {
            if (File.Exists(item.FinalPath)) File.Delete(item.FinalPath);
            File.Move(item.PartPath, item.FinalPath);
            SidecarStore.Delete(item.FinalPath);

            OnUi(() => item.State = TransferState.Completed);
            _log.Info("Download", "Completed " + item.RelativePath + " ("
                + ByteFormatter.Format(item.DownloadedBytes) + ").");
        }
        catch (Exception ex)
        {
            _log.Error("Download", "Downloaded " + item.FileName
                + " but could not rename it into place.", ex);
            OnUi(() =>
            {
                item.ErrorMessage = "Downloaded, but renaming it failed: " + ex.Message;
                item.TechnicalDetails = ex.GetType().Name;
                item.State = TransferState.Failed;
            });
        }
    }

    private void CheckTorrentCompletion(long magnetId)
    {
        var forTorrent = Transfers.Where(t => t.MagnetId == magnetId).ToList();
        if (forTorrent.Count == 0) return;
        if (forTorrent.Any(t => !t.IsFinished)) return;
        if (forTorrent.All(t => t.State == TransferState.Completed))
            TorrentCompleted?.Invoke(magnetId);
    }

    // -----------------------------------------------------------------------
    // Controls
    // -----------------------------------------------------------------------

    public void Pause(TransferItem item)
    {
        item.PauseRequested = true;
        if (item.State == TransferState.Queued)
        {
            item.State = TransferState.Paused;
            return;
        }
        item.Cancellation?.Cancel();
    }

    public void Resume(TransferItem item)
    {
        if (!item.CanResume) return;
        item.PauseRequested = false;
        item.ResetForRetry();
        StartWorker(item);
    }

    public void Cancel(TransferItem item, bool deletePartial)
    {
        item.PauseRequested = false;

        if (item.State is TransferState.Queued or TransferState.Paused)
            item.State = TransferState.Cancelled;
        else
            item.Cancellation?.Cancel();

        if (!deletePartial) return;

        // Give the worker a moment to release its handles before deleting.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            TryDelete(item.PartPath);
            SidecarStore.Delete(item.FinalPath);
        });
    }

    public void PauseAll()
    {
        IsPausedGlobally = true;
        foreach (var item in Transfers.Where(t => t.CanPause).ToList()) Pause(item);
    }

    public void ResumeAll()
    {
        IsPausedGlobally = false;
        foreach (var item in Transfers.Where(t => t.State == TransferState.Paused).ToList())
            Resume(item);
    }

    /// <summary>Cancel everything. Completed files are never touched.</summary>
    public void CancelAll(bool deletePartials)
    {
        var previous = _globalCts;
        _globalCts = new CancellationTokenSource();
        previous.Cancel();

        foreach (var item in Transfers.Where(t => !t.IsFinished).ToList())
        {
            item.PauseRequested = false;
            if (item.State is TransferState.Queued or TransferState.Paused)
                item.State = TransferState.Cancelled;
            if (deletePartials)
            {
                TryDelete(item.PartPath);
                SidecarStore.Delete(item.FinalPath);
            }
        }

        previous.Dispose();
    }

    public void ClearFinished()
    {
        foreach (var item in Transfers.Where(t => t.IsFinished).ToList())
            Transfers.Remove(item);
        _dirtyAggregate = true;
        AggregateChanged?.Invoke();
    }

    /// <summary>
    /// Pause everything and flush resume state. Called on window close so a long
    /// transfer is not lost to a stray Alt+F4.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _uiTimer.Stop();

        foreach (var item in Transfers.Where(t => t.IsActive).ToList())
        {
            item.PauseRequested = true;
            item.Cancellation?.Cancel();
        }

        // Wait briefly for the workers to write their sidecars.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            lock (_queueSync) { if (_running.Count == 0) break; }
            await Task.Delay(150).ConfigureAwait(false);
        }

        _log.Info("Download", "Download manager shut down.");
    }

    // -----------------------------------------------------------------------
    // Resume discovery
    // -----------------------------------------------------------------------

    /// <summary>
    /// Find interrupted downloads under the output folder by their sidecars, so they can
    /// be offered on startup.
    /// </summary>
    public static List<(string FinalPath, PartSidecar Sidecar)> FindInterrupted(
        string downloadDirectory, Logger? log = null)
    {
        var found = new List<(string, PartSidecar)>();

        if (string.IsNullOrWhiteSpace(downloadDirectory) || !Directory.Exists(downloadDirectory))
            return found;

        try
        {
            foreach (var sidecarPath in Directory.EnumerateFiles(
                downloadDirectory, "*" + SidecarStore.SidecarExtension, SearchOption.AllDirectories))
            {
                var finalPath = SidecarStore.FinalPathFromSidecar(sidecarPath);
                var sidecar = SidecarStore.TryLoad(finalPath);

                if (sidecar is null) continue;
                if (!File.Exists(SidecarStore.PartPathFor(finalPath))) continue;
                if (sidecar.IsComplete) continue;

                found.Add((finalPath, sidecar));
            }
        }
        catch (Exception ex)
        {
            log?.Warn("Download", "Could not scan for interrupted downloads: " + ex.Message);
        }

        return found;
    }

    /// <summary>Turn a discovered sidecar back into a queueable transfer.</summary>
    public static TransferItem? ToTransfer(string finalPath, PartSidecar sidecar, string downloadRoot)
    {
        if (string.IsNullOrWhiteSpace(sidecar.SourceLink)) return null;

        var relative = PathHelper.IsInside(downloadRoot, finalPath)
            ? Path.GetRelativePath(downloadRoot, finalPath)
            : Path.GetFileName(finalPath);

        return new TransferItem
        {
            SourceLink = sidecar.SourceLink!,
            FinalPath = finalPath,
            FileName = Path.GetFileName(finalPath),
            RelativePath = relative,
            MagnetId = sidecar.MagnetId,
            TorrentName = sidecar.TorrentName ?? "(unknown)",
            ExpectedSize = sidecar.TotalSize
        };
    }

    /// <summary>
    /// Free space on the volume holding <paramref name="path"/>, or null when that
    /// cannot be determined. A path that is not rooted is rejected rather than resolved
    /// against the current directory, which would report a different drive's space.
    /// </summary>
    public static long? GetFreeSpace(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;

            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void OnUi(Action action) => UiDispatch.Invoke(action);

    public void Dispose()
    {
        _uiTimer.Stop();
        _globalCts.Cancel();
        _globalCts.Dispose();
        _http.Dispose();
        _handler.Dispose();
        _slots.Dispose();
    }
}
