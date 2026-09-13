using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Models;

namespace AllDebridDownloader.Services;

/// <summary>Why a download stopped.</summary>
public enum DownloadOutcome { Completed, Cancelled, Paused, Failed }

public sealed class DownloadResult
{
    public DownloadOutcome Outcome { get; init; }
    public string? ErrorMessage { get; init; }
    public string? TechnicalDetails { get; init; }
    public long BytesDownloaded { get; init; }
}

/// <summary>Probe of the remote file before any bytes are fetched.</summary>
public sealed class RemoteFileInfo
{
    public long? ContentLength { get; init; }
    public bool SupportsRanges { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
}

/// <summary>
/// Downloads one file using parallel HTTP Range requests written straight into the
/// final ".part" file at their own offsets. Handles per-segment retry with backoff,
/// stall detection, resume from a sidecar, and length verification.
///
/// This is the piece that makes the app more robust than a plain single-stream GET:
/// one flaky segment retries on its own rather than restarting a 40 GB transfer.
/// </summary>
public sealed class SegmentedFileDownloader
{
    /// <summary>Below this size, parallelism costs more than it gains.</summary>
    public const long MinimumSizeForSegmentation = 16L * 1024 * 1024;

    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Attempts per segment, not per file: one flaky segment must not restart a 40 GB
    /// transfer. Settable so tests need not sit through the full backoff ladder.
    /// </summary>
    public int MaxAttemptsPerSegment { get; init; } = 5;

    /// <summary>
    /// A segment that receives nothing for this long is considered wedged, even though
    /// the connection is technically still open. This is the failure mode that simple
    /// downloaders handle worst. Settable so tests need not wait a real minute.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Logger _log;
    private readonly SpeedLimiter _limiter;

    public SegmentedFileDownloader(HttpClient http, Logger log, SpeedLimiter limiter)
    {
        _http = http;
        _log = log;
        _limiter = limiter;
    }

    /// <summary>Raised as bytes land, for progress display. Called from worker threads.</summary>
    public event Action<long>? BytesReceived;

    // -----------------------------------------------------------------------
    // Probe
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve the URL and probe the file, retrying transient failures and re-resolving
    /// once if the link has already expired. Without this a single blip before the first
    /// byte would fail the whole file, since the per-segment retry has not started yet.
    /// </summary>
    private async Task<(string Url, RemoteFileInfo Info)> ResolveAndProbeWithRetryAsync(
        Func<bool, CancellationToken, Task<string>> resolveUrl, CancellationToken ct)
    {
        var refreshed = false;

        for (var attempt = 1; attempt <= MaxAttemptsPerSegment; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var url = await resolveUrl(refreshed, ct).ConfigureAwait(false);

            try
            {
                var info = await ProbeAsync(url, ct).ConfigureAwait(false);
                return (url, info);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ExpiredLinkException)
            {
                if (refreshed) throw;
                refreshed = true;
                _log.Warn("Download", "Download URL had already expired; unlocking it again.");
                attempt--;   // the refresh is not one of the real attempts
            }
            catch (PermanentHttpException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (attempt == MaxAttemptsPerSegment) throw;

                var delay = Backoff(attempt);
                _log.Warn("Download", "Probe attempt " + attempt + "/" + MaxAttemptsPerSegment
                    + " failed (" + ex.GetType().Name + "); retrying in "
                    + delay.TotalSeconds.ToString("0.#") + "s.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw new HttpRequestException("Could not probe the download URL.");
    }

    /// <summary>
    /// Learn the size and whether Range is honoured. A HEAD is tried first; some CDNs
    /// answer HEAD poorly, so it falls back to a one-byte ranged GET, whose 206 is
    /// positive proof that ranges work.
    /// </summary>
    public async Task<RemoteFileInfo> ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await _http
                .SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (headResponse.IsSuccessStatusCode)
            {
                var length = headResponse.Content.Headers.ContentLength;
                var acceptsRanges = headResponse.Headers.AcceptRanges.Contains("bytes");

                // An authoritative zero length needs no range probe -- and asking for
                // bytes 0-0 of an empty file earns a 416 from a correct server.
                if (length == 0)
                {
                    return new RemoteFileInfo
                    {
                        ContentLength = 0,
                        SupportsRanges = false,
                        ETag = headResponse.Headers.ETag?.ToString(),
                        LastModified = headResponse.Content.Headers.LastModified?.ToString("R")
                    };
                }

                if (length is > 0 && acceptsRanges)
                {
                    return new RemoteFileInfo
                    {
                        ContentLength = length,
                        SupportsRanges = true,
                        ETag = headResponse.Headers.ETag?.ToString(),
                        LastModified = headResponse.Content.Headers.LastModified?.ToString("R")
                    };
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug("Download", "HEAD probe failed, falling back to a ranged GET: " + ex.GetType().Name);
        }

        // Ranged GET probe: a 206 proves ranges, a 200 proves they are ignored.
        using var probe = new HttpRequestMessage(HttpMethod.Get, url);
        probe.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await _http
            .SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // 416 means the range was unsatisfiable, which for a 0-0 probe means an empty
        // file. Treat it as a known-empty file rather than a failure.
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new RemoteFileInfo
            {
                ContentLength = response.Content.Headers.ContentRange?.Length ?? 0,
                SupportsRanges = false,
                ETag = response.Headers.ETag?.ToString(),
                LastModified = response.Content.Headers.LastModified?.ToString("R")
            };
        }

        ThrowIfPermanentFailure(response);
        response.EnsureSuccessStatusCode();

        var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent;

        long? total = null;
        if (supportsRanges && response.Content.Headers.ContentRange?.Length is { } len)
            total = len;
        else if (!supportsRanges)
            total = response.Content.Headers.ContentLength;

        return new RemoteFileInfo
        {
            ContentLength = total,
            SupportsRanges = supportsRanges,
            ETag = response.Headers.ETag?.ToString(),
            LastModified = response.Content.Headers.LastModified?.ToString("R")
        };
    }

    // -----------------------------------------------------------------------
    // Download
    // -----------------------------------------------------------------------

    /// <summary>
    /// Download to <paramref name="partPath"/> according to <paramref name="sidecar"/>,
    /// which is updated in place and persisted as progress is made.
    /// </summary>
    /// <param name="resolveUrl">
    /// Supplies the download URL; called with forceRefresh=true when a cached unlocked
    /// URL has expired mid-transfer.
    /// </param>
    public async Task<DownloadResult> DownloadAsync(
        string partPath,
        string finalPath,
        PartSidecar sidecar,
        Func<bool, CancellationToken, Task<string>> resolveUrl,
        int connectionsPerFile,
        CancellationToken ct)
    {
        var startedWith = sidecar.DownloadedBytes;

        try
        {
            var (url, info) = await ResolveAndProbeWithRetryAsync(resolveUrl, ct)
                .ConfigureAwait(false);

            // If the remote file is not the one the sidecar describes, start over rather
            // than stitching together two different files.
            if (sidecar.Segments.Count > 0
                && !sidecar.MatchesRemote(info.ContentLength, info.ETag, info.LastModified))
            {
                _log.Warn("Download", Path.GetFileName(finalPath)
                    + " changed on the server since it was interrupted; restarting it.");
                sidecar.Segments.Clear();
                TryDelete(partPath);
            }

            sidecar.SupportsRanges = info.SupportsRanges;
            if (info.ContentLength is > 0) sidecar.TotalSize = info.ContentLength.Value;
            sidecar.ETag ??= info.ETag;
            sidecar.LastModified ??= info.LastModified;

            // Plan segments on first run; a resume keeps the existing plan.
            if (sidecar.Segments.Count == 0)
            {
                var useSegments = info.SupportsRanges
                    && sidecar.TotalSize >= MinimumSizeForSegmentation
                    && connectionsPerFile > 1;

                sidecar.Segments = sidecar.TotalSize > 0
                    ? SidecarStore.PlanSegments(sidecar.TotalSize, useSegments ? connectionsPerFile : 1)
                    : new List<SegmentRecord> { new() { Start = 0, End = -1 } };
            }

            PreallocateAndTruncate(partPath, sidecar);

            await SidecarStore.SaveAsync(finalPath, sidecar, ct).ConfigureAwait(false);

            // Unknown length, or ranges unsupported: one sequential stream.
            if (sidecar.TotalSize <= 0 || !info.SupportsRanges || sidecar.Segments.Count == 1)
            {
                await DownloadSingleStreamAsync(partPath, finalPath, sidecar, resolveUrl, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await DownloadSegmentsAsync(partPath, finalPath, sidecar, resolveUrl, ct)
                    .ConfigureAwait(false);
            }

            await SidecarStore.SaveAsync(finalPath, sidecar, ct).ConfigureAwait(false);

            // Verify before the rename, so a short file is never presented as finished.
            // This must count bytes actually received: the .part file is pre-allocated to
            // the full length, so its size on disk always matches and proves nothing.
            if (sidecar.TotalSize > 0)
            {
                var received = sidecar.DownloadedBytes;
                if (received != sidecar.TotalSize)
                {
                    return new DownloadResult
                    {
                        Outcome = DownloadOutcome.Failed,
                        ErrorMessage = "Size mismatch -- expected "
                            + ByteFormatter.Format(sidecar.TotalSize)
                            + ", got " + ByteFormatter.Format(received) + ".",
                        TechnicalDetails = "expected=" + sidecar.TotalSize
                            + " received=" + received
                            + " onDisk=" + new FileInfo(partPath).Length,
                        BytesDownloaded = sidecar.DownloadedBytes - startedWith
                    };
                }
            }

            return new DownloadResult
            {
                Outcome = DownloadOutcome.Completed,
                BytesDownloaded = sidecar.DownloadedBytes - startedWith
            };
        }
        catch (OperationCanceledException)
        {
            await TrySaveSidecarAsync(finalPath, sidecar).ConfigureAwait(false);
            return new DownloadResult
            {
                Outcome = DownloadOutcome.Cancelled,
                BytesDownloaded = sidecar.DownloadedBytes - startedWith
            };
        }
        catch (AllDebridApiException ex)
        {
            await TrySaveSidecarAsync(finalPath, sidecar).ConfigureAwait(false);
            return new DownloadResult
            {
                Outcome = DownloadOutcome.Failed,
                ErrorMessage = ex.Message,
                TechnicalDetails = ex.TechnicalDetails,
                BytesDownloaded = sidecar.DownloadedBytes - startedWith
            };
        }
        catch (Exception ex)
        {
            await TrySaveSidecarAsync(finalPath, sidecar).ConfigureAwait(false);
            _log.Error("Download", "Failed downloading " + Path.GetFileName(finalPath) + ".", ex);
            return new DownloadResult
            {
                Outcome = DownloadOutcome.Failed,
                ErrorMessage = FriendlyIoMessage(ex),
                TechnicalDetails = ex.GetType().Name + ": " + ex.Message,
                BytesDownloaded = sidecar.DownloadedBytes - startedWith
            };
        }
    }

    /// <summary>
    /// Create the .part file at its full length up front so segments never have to grow
    /// it, and drop any tail left over from a previous, larger attempt.
    /// </summary>
    private static void PreallocateAndTruncate(string partPath, PartSidecar sidecar)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);

        using var fs = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.ReadWrite);

        if (sidecar.TotalSize > 0 && fs.Length != sidecar.TotalSize)
            fs.SetLength(sidecar.TotalSize);
    }

    private async Task TrySaveSidecarAsync(string finalPath, PartSidecar sidecar)
    {
        try
        {
            await SidecarStore.SaveAsync(finalPath, sidecar, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Download", "Could not save resume state for "
                + Path.GetFileName(finalPath) + ": " + ex.GetType().Name);
        }
    }

    // -----------------------------------------------------------------------
    // Parallel path
    // -----------------------------------------------------------------------

    private async Task DownloadSegmentsAsync(
        string partPath,
        string finalPath,
        PartSidecar sidecar,
        Func<bool, CancellationToken, Task<string>> resolveUrl,
        CancellationToken ct)
    {
        var pending = sidecar.Segments.Where(s => !s.IsComplete).ToList();
        if (pending.Count == 0) return;

        using var sidecarFlush = new Timer(_ => _ = TrySaveSidecarAsync(finalPath, sidecar),
            null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = pending.Select(segment =>
            DownloadSegmentWithRetryAsync(partPath, segment, resolveUrl, linked.Token)).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // One segment failing for good makes the rest pointless; stop them promptly
            // so the file's state is flushed and the UI can show the real error.
            await linked.CancelAsync().ConfigureAwait(false);

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch { /* surfaced below */ }

            var firstFailure = tasks.FirstOrDefault(t => t.IsFaulted)?.Exception?.InnerExceptions
                .FirstOrDefault(e => e is not OperationCanceledException);

            if (firstFailure is not null) throw firstFailure;
            throw;
        }
    }

    private async Task DownloadSegmentWithRetryAsync(
        string partPath,
        SegmentRecord segment,
        Func<bool, CancellationToken, Task<string>> resolveUrl,
        CancellationToken ct)
    {
        var refreshedUrl = false;

        for (var attempt = 1; attempt <= MaxAttemptsPerSegment; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (segment.IsComplete) return;

            try
            {
                var url = await resolveUrl(refreshedUrl, ct).ConfigureAwait(false);
                await TransferSegmentAsync(partPath, segment, url, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ExpiredLinkException)
            {
                // The unlocked URL went stale mid-transfer. Re-unlock once, then treat a
                // second failure as real.
                if (refreshedUrl) throw;
                refreshedUrl = true;
                _log.Warn("Download", "Download URL expired; unlocking it again.");
                attempt--;   // the refresh is not one of the five real attempts
            }
            catch (PermanentHttpException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (attempt == MaxAttemptsPerSegment)
                {
                    _log.Error("Download", "Segment " + segment.Start + "-" + segment.End
                        + " failed after " + MaxAttemptsPerSegment + " attempts.", ex);
                    throw;
                }

                var delay = Backoff(attempt);
                _log.Warn("Download", "Segment " + segment.Start + "-" + segment.End
                    + " attempt " + attempt + "/" + MaxAttemptsPerSegment + " failed ("
                    + ex.GetType().Name + "); retrying in "
                    + delay.TotalSeconds.ToString("0.#") + "s.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Fetch the not-yet-written remainder of one segment straight into the .part file
    /// at its own offset.
    /// </summary>
    private async Task TransferSegmentAsync(
        string partPath, SegmentRecord segment, string url, CancellationToken ct)
    {
        var from = segment.NextOffset;
        if (from > segment.End) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, segment.End);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        ThrowIfPermanentFailure(response);

        // A server that answers 200 to a ranged request is sending the whole file; that
        // would corrupt every segment but the first.
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            if (segment.Start == 0 && response.IsSuccessStatusCode)
            {
                _log.Warn("Download", "Server ignored the Range header; falling back to one stream.");
            }
            else
            {
                throw new HttpRequestException(
                    "Expected 206 Partial Content for a ranged request, got "
                    + (int)response.StatusCode + ".");
            }
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(partPath, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite, BufferSize, useAsync: true);

        file.Seek(from, SeekOrigin.Begin);

        await PumpAsync(stream, file, segment, ct).ConfigureAwait(false);
        await file.FlushAsync(ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Sequential path (no range support, or unknown length)
    // -----------------------------------------------------------------------

    private async Task DownloadSingleStreamAsync(
        string partPath,
        string finalPath,
        PartSidecar sidecar,
        Func<bool, CancellationToken, Task<string>> resolveUrl,
        CancellationToken ct)
    {
        var segment = sidecar.Segments[0];
        var refreshedUrl = false;

        for (var attempt = 1; attempt <= MaxAttemptsPerSegment; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var url = await resolveUrl(refreshedUrl, ct).ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                // Resume a sequential transfer with an open-ended range when we can.
                var resumeFrom = segment.Written;
                if (resumeFrom > 0 && sidecar.SupportsRanges)
                    request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                ThrowIfPermanentFailure(response);
                response.EnsureSuccessStatusCode();

                // The server ignored our resume request: start from zero.
                if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    _log.Warn("Download", "Server would not resume "
                        + Path.GetFileName(finalPath) + "; restarting the file.");
                    segment.Written = 0;
                    resumeFrom = 0;
                }

                if (sidecar.TotalSize <= 0 && response.Content.Headers.ContentLength is > 0)
                {
                    sidecar.TotalSize = response.Content.Headers.ContentLength!.Value
                        + (response.StatusCode == HttpStatusCode.PartialContent ? resumeFrom : 0);
                    segment.End = sidecar.TotalSize - 1;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);
                await using var file = new FileStream(partPath, FileMode.OpenOrCreate,
                    FileAccess.Write, FileShare.ReadWrite, BufferSize, useAsync: true);

                file.Seek(resumeFrom, SeekOrigin.Begin);

                await PumpAsync(stream, file, segment, ct, openEnded: segment.End < 0)
                    .ConfigureAwait(false);

                await file.FlushAsync(ct).ConfigureAwait(false);

                // Unknown-length transfers only know their size once the stream ends.
                if (sidecar.TotalSize <= 0)
                {
                    sidecar.TotalSize = segment.Written;
                    segment.End = segment.Written - 1;
                }

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ExpiredLinkException)
            {
                if (refreshedUrl) throw;
                refreshedUrl = true;
                attempt--;
            }
            catch (PermanentHttpException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (attempt == MaxAttemptsPerSegment) throw;
                var delay = Backoff(attempt);
                _log.Warn("Download", Path.GetFileName(finalPath) + " attempt " + attempt
                    + " failed (" + ex.GetType().Name + "); retrying in "
                    + delay.TotalSeconds.ToString("0.#") + "s.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Shared byte pump, with stall detection and throttling
    // -----------------------------------------------------------------------

    private async Task PumpAsync(
        Stream source, Stream destination, SegmentRecord segment,
        CancellationToken ct, bool openEnded = false)
    {
        var buffer = new byte[BufferSize];
        var lastProgress = Stopwatch.GetTimestamp();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = openEnded ? buffer.Length : (int)Math.Min(buffer.Length, segment.Remaining);
            if (remaining <= 0) break;

            // Honour the global speed cap before asking for more bytes.
            var allowed = await _limiter.AcquireAsync(remaining, ct).ConfigureAwait(false);

            // A read that produces nothing for StallTimeout means the connection is
            // wedged even though it is technically still open.
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stallCts.CancelAfter(StallTimeout);

            int read;
            try
            {
                read = await source.ReadAsync(buffer.AsMemory(0, allowed), stallCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var idle = (Stopwatch.GetTimestamp() - lastProgress) / (double)Stopwatch.Frequency;
                throw new StalledException("No data for "
                    + idle.ToString("0") + "s; treating the connection as stalled.");
            }

            if (read == 0) break;   // end of stream

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

            segment.Written += read;
            lastProgress = Stopwatch.GetTimestamp();
            BytesReceived?.Invoke(read);
        }
    }

    // -----------------------------------------------------------------------
    // Error classification
    // -----------------------------------------------------------------------

    private static void ThrowIfPermanentFailure(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;

        // An expired unlocked link looks like this; the caller re-unlocks once.
        if (code is 403 or 404 or 410)
            throw new ExpiredLinkException("Download URL returned " + code + ".");

        if (code is 400 or 401 or 402 or 405 or 451)
            throw new PermanentHttpException("Download URL returned " + code + ".");
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        StalledException => true,
        HttpRequestException => true,
        UnauthorizedAccessException => false,
        IOException io => !IsFatalIo(io),
        TimeoutException => true,
        OperationCanceledException => true,   // a stall CTS, not the caller's token
        _ => false
    };

    /// <summary>Disk full or access denied will not fix itself by retrying.</summary>
    private static bool IsFatalIo(IOException ex)
    {
        // 0x70 ERROR_DISK_FULL, 0x27 ERROR_HANDLE_DISK_FULL.
        // UnauthorizedAccessException is not an IOException, so it is classified
        // separately by the callers rather than here.
        var hr = ex.HResult & 0xFFFF;
        return hr is 0x70 or 0x27;
    }

    private static string FriendlyIoMessage(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Windows denied access to that file or folder.",
        IOException io when IsFatalIo(io) => "The drive is full.",
        IOException io => "A disk error occurred: " + io.Message,
        _ => "The download failed: " + ex.Message
    };

    private static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(16, Math.Pow(2, attempt - 1));
        var jitter = Random.Shared.NextDouble() * 0.4 - 0.2;   // +/-20%
        return TimeSpan.FromSeconds(Math.Max(0.5, seconds * (1 + jitter)));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

/// <summary>The unlocked URL has expired and needs regenerating.</summary>
public sealed class ExpiredLinkException(string message) : Exception(message);

/// <summary>An HTTP status that retrying will not fix.</summary>
public sealed class PermanentHttpException(string message) : Exception(message);

/// <summary>The connection is open but has delivered nothing for too long.</summary>
public sealed class StalledException(string message) : Exception(message);
