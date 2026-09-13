using System.Net.Http.Headers;
using System.Text;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Models;
using AllDebridDownloader.Services;

namespace AllDebridDownloader.Tests;

public sealed class SegmentPlanningTests
{
    [Theory]
    [InlineData(1000, 4)]
    [InlineData(1000, 3)]     // not divisible: remainder spread over early segments
    [InlineData(1001, 8)]
    [InlineData(7, 8)]        // more segments requested than bytes available
    [InlineData(1, 1)]
    [InlineData(1, 8)]
    [InlineData(100_000_000_000, 16)]
    public void Segments_cover_every_byte_exactly_once(long size, int count)
    {
        var segments = SidecarStore.PlanSegments(size, count);

        Assert.NotEmpty(segments);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(size - 1, segments[^1].End);
        Assert.Equal(size, segments.Sum(s => s.Length));

        // Contiguous, no gaps and no overlaps.
        for (var i = 1; i < segments.Count; i++)
            Assert.Equal(segments[i - 1].End + 1, segments[i].Start);

        Assert.All(segments, s => Assert.True(s.Length > 0));
    }

    [Fact]
    public void Remainder_bytes_go_to_the_earlier_segments()
    {
        var segments = SidecarStore.PlanSegments(10, 3);

        Assert.Equal(3, segments.Count);
        Assert.Equal(4, segments[0].Length);
        Assert.Equal(3, segments[1].Length);
        Assert.Equal(3, segments[2].Length);
    }

    [Fact]
    public void Never_plans_more_segments_than_bytes()
    {
        var segments = SidecarStore.PlanSegments(3, 16);
        Assert.Equal(3, segments.Count);
    }

    [Fact]
    public void A_single_byte_file_gets_one_segment()
    {
        var segments = SidecarStore.PlanSegments(1, 8);

        Assert.Single(segments);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(0, segments[0].End);
        Assert.Equal(1, segments[0].Length);
    }

    [Fact]
    public void A_zero_byte_file_gets_one_open_ended_segment()
    {
        var segments = SidecarStore.PlanSegments(0, 8);

        Assert.Single(segments);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(-1, segments[0].End);
    }

    [Fact]
    public void Segment_progress_arithmetic()
    {
        var segment = new SegmentRecord { Start = 100, End = 199 };

        Assert.Equal(100, segment.Length);
        Assert.Equal(100, segment.Remaining);
        Assert.Equal(100, segment.NextOffset);
        Assert.False(segment.IsComplete);

        segment.Written = 40;
        Assert.Equal(60, segment.Remaining);
        Assert.Equal(140, segment.NextOffset);   // where the next byte belongs in the file
        Assert.False(segment.IsComplete);

        segment.Written = 100;
        Assert.Equal(0, segment.Remaining);
        Assert.True(segment.IsComplete);
    }
}

public sealed class SidecarTests
{
    private static PartSidecar MakeSidecar(long size, int segments) => new()
    {
        SourceLink = "https://alldebrid.com/f/abc",
        FinalFileName = "movie.mkv",
        TorrentName = "Some.Show",
        MagnetId = 123,
        TotalSize = size,
        SupportsRanges = true,
        ETag = "\"abc123\"",
        LastModified = "Wed, 21 Oct 2026 07:28:00 GMT",
        Segments = SidecarStore.PlanSegments(size, segments)
    };

    [Fact]
    public async Task Sidecar_round_trips_through_disk()
    {
        using var temp = new TempDir();
        var finalPath = temp.File("movie.mkv");

        var original = MakeSidecar(1_000_000, 4);
        original.Segments[0].Written = 1234;

        await SidecarStore.SaveAsync(finalPath, original);
        var loaded = SidecarStore.TryLoad(finalPath);

        Assert.NotNull(loaded);
        Assert.Equal(original.TotalSize, loaded.TotalSize);
        Assert.Equal(original.SourceLink, loaded.SourceLink);
        Assert.Equal(original.ETag, loaded.ETag);
        Assert.Equal(4, loaded.Segments.Count);
        Assert.Equal(1234, loaded.Segments[0].Written);
        Assert.Equal(1234, loaded.DownloadedBytes);
    }

    [Fact]
    public void A_missing_sidecar_loads_as_null()
    {
        using var temp = new TempDir();
        Assert.Null(SidecarStore.TryLoad(temp.File("nothing.mkv")));
    }

    [Fact]
    public async Task A_truncated_sidecar_loads_as_null_rather_than_throwing()
    {
        using var temp = new TempDir();
        var finalPath = temp.File("movie.mkv");

        await SidecarStore.SaveAsync(finalPath, MakeSidecar(1000, 2));

        var sidecarPath = SidecarStore.SidecarPathFor(finalPath);
        var text = await File.ReadAllTextAsync(sidecarPath);
        await File.WriteAllTextAsync(sidecarPath, text[..(text.Length / 2)]);

        // A corrupt sidecar must mean "restart cleanly", not a crash.
        Assert.Null(SidecarStore.TryLoad(finalPath));
    }

    [Fact]
    public async Task An_inconsistent_sidecar_is_rejected()
    {
        using var temp = new TempDir();
        var finalPath = temp.File("movie.mkv");

        var sidecar = MakeSidecar(1000, 2);
        sidecar.Segments[1].Start = 999;   // leaves a gap: no longer contiguous
        await SidecarStore.SaveAsync(finalPath, sidecar);

        Assert.Null(SidecarStore.TryLoad(finalPath));
    }

    [Fact]
    public void Self_consistency_checks_coverage_and_bounds()
    {
        var good = MakeSidecar(1000, 4);
        Assert.True(good.IsSelfConsistent());

        var gap = MakeSidecar(1000, 4);
        gap.Segments[2].Start += 5;
        Assert.False(gap.IsSelfConsistent());

        var overWritten = MakeSidecar(1000, 4);
        overWritten.Segments[0].Written = 99999;
        Assert.False(overWritten.IsSelfConsistent());

        var shortCoverage = MakeSidecar(1000, 4);
        shortCoverage.Segments.RemoveAt(3);
        Assert.False(shortCoverage.IsSelfConsistent());

        Assert.False(new PartSidecar().IsSelfConsistent());
    }

    [Fact]
    public void Matches_remote_accepts_an_unchanged_file()
    {
        var sidecar = MakeSidecar(1000, 2);

        Assert.True(sidecar.MatchesRemote(1000, "\"abc123\"",
            "Wed, 21 Oct 2026 07:28:00 GMT"));
    }

    [Fact]
    public void A_changed_etag_forces_a_restart()
    {
        var sidecar = MakeSidecar(1000, 2);
        Assert.False(sidecar.MatchesRemote(1000, "\"different\"", null));
    }

    [Fact]
    public void A_changed_size_forces_a_restart()
    {
        var sidecar = MakeSidecar(1000, 2);
        Assert.False(sidecar.MatchesRemote(2000, "\"abc123\"", null));
    }

    [Fact]
    public void A_changed_last_modified_forces_a_restart()
    {
        var sidecar = MakeSidecar(1000, 2);
        Assert.False(sidecar.MatchesRemote(1000, null, "Thu, 22 Oct 2026 07:28:00 GMT"));
    }

    [Fact]
    public void Validators_the_server_no_longer_sends_do_not_force_a_restart()
    {
        // Some CDNs drop ETag on a later request; that alone is not evidence of change.
        var sidecar = MakeSidecar(1000, 2);
        Assert.True(sidecar.MatchesRemote(1000, null, null));
    }

    [Fact]
    public void Completion_is_all_segments_written()
    {
        var sidecar = MakeSidecar(1000, 4);
        Assert.False(sidecar.IsComplete);

        foreach (var s in sidecar.Segments) s.Written = s.Length;

        Assert.True(sidecar.IsComplete);
        Assert.Equal(1000, sidecar.DownloadedBytes);
    }

    [Fact]
    public void Part_and_sidecar_paths_derive_from_the_final_path()
    {
        var final = Path.Combine("C:\\D", "movie.mkv");

        Assert.Equal(final + ".part", SidecarStore.PartPathFor(final));
        Assert.Equal(final + ".part.json", SidecarStore.SidecarPathFor(final));
        Assert.Equal(final, SidecarStore.FinalPathFromSidecar(final + ".part.json"));
    }

    [Fact]
    public async Task Delete_removes_the_sidecar_and_its_temp_file()
    {
        using var temp = new TempDir();
        var finalPath = temp.File("movie.mkv");

        await SidecarStore.SaveAsync(finalPath, MakeSidecar(1000, 2));
        Assert.True(File.Exists(SidecarStore.SidecarPathFor(finalPath)));

        SidecarStore.Delete(finalPath);
        Assert.False(File.Exists(SidecarStore.SidecarPathFor(finalPath)));
    }
}

public sealed class SegmentedDownloaderTests : IDisposable
{
    private readonly TestLogger _log = new();

    public void Dispose() => _log.Dispose();

    /// <summary>Serves a byte array, honouring Range requests like a real CDN.</summary>
    private static FakeHandler RangeServer(byte[] content, bool supportRanges = true,
        string? etag = "\"v1\"")
    {
        return new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                head.Content.Headers.ContentLength = content.Length;
                if (supportRanges) head.Headers.AcceptRanges.Add("bytes");
                if (etag is not null) head.Headers.TryAddWithoutValidation("ETag", etag);
                return head;
            }

            var range = request.Headers.Range?.Ranges.FirstOrDefault();

            if (!supportRanges || range is null)
            {
                var whole = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                };
                if (etag is not null) whole.Headers.TryAddWithoutValidation("ETag", etag);
                return whole;
            }

            var from = (int)(range.From ?? 0);
            var to = (int)(range.To ?? content.Length - 1);
            to = Math.Min(to, content.Length - 1);
            var slice = content[from..(to + 1)];

            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice)
            };
            partial.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(from, to, content.Length);
            if (etag is not null) partial.Headers.TryAddWithoutValidation("ETag", etag);
            return partial;
        });
    }

    private static byte[] MakeContent(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++) data[i] = (byte)(i % 251);
        return data;
    }

    private SegmentedFileDownloader MakeDownloader(FakeHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new SegmentedFileDownloader(http, _log.Logger, new SpeedLimiter(0));
    }

    // -----------------------------------------------------------------------
    // Probe
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Probe_detects_range_support_from_head()
    {
        var handler = RangeServer(MakeContent(5000));
        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var info = await downloader.ProbeAsync("https://cdn/file", CancellationToken.None);

            Assert.True(info.SupportsRanges);
            Assert.Equal(5000, info.ContentLength);
            Assert.Equal("\"v1\"", info.ETag);
        }
    }

    [Fact]
    public async Task Probe_detects_missing_range_support_from_a_200()
    {
        // A server that answers 200 to a ranged request is ignoring Range entirely.
        var handler = RangeServer(MakeContent(5000), supportRanges: false);
        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var info = await downloader.ProbeAsync("https://cdn/file", CancellationToken.None);

            Assert.False(info.SupportsRanges);
            Assert.Equal(5000, info.ContentLength);
        }
    }

    [Fact]
    public async Task Probe_falls_back_to_a_ranged_get_when_head_fails()
    {
        var content = MakeContent(4096);
        var handler = new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                throw new HttpRequestException("HEAD not allowed");

            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[..1])
            };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, content.Length);
            return partial;
        });

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var info = await downloader.ProbeAsync("https://cdn/file", CancellationToken.None);

            Assert.True(info.SupportsRanges);
            Assert.Equal(4096, info.ContentLength);
        }
    }

    // -----------------------------------------------------------------------
    // Whole-file downloads
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Segmented_download_reassembles_the_file_byte_for_byte()
    {
        // Above the 16 MB segmentation threshold so the parallel path is exercised.
        var content = MakeContent(20 * 1024 * 1024);
        var handler = RangeServer(content);

        using var temp = new TempDir();
        var finalPath = temp.File("movie.mkv");
        var sidecar = new PartSidecar { SourceLink = "https://alldebrid.com/f/x" };

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
                (_, _) => Task.FromResult("https://cdn/file"),
                connectionsPerFile: 8,
                CancellationToken.None);

            Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        }

        var written = await File.ReadAllBytesAsync(SidecarStore.PartPathFor(finalPath));
        Assert.Equal(content.Length, written.Length);
        Assert.True(content.SequenceEqual(written), "The reassembled file differs from the source.");
        Assert.Equal(8, sidecar.Segments.Count);
        Assert.True(sidecar.IsComplete);
    }

    [Fact]
    public async Task Small_files_use_a_single_stream()
    {
        var content = MakeContent(1000);
        var handler = RangeServer(content);

        using var temp = new TempDir();
        var finalPath = temp.File("small.txt");
        var sidecar = new PartSidecar { SourceLink = "l" };

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
                (_, _) => Task.FromResult("https://cdn/file"), 8, CancellationToken.None);

            Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        }

        Assert.Single(sidecar.Segments);
        var written = await File.ReadAllBytesAsync(SidecarStore.PartPathFor(finalPath));
        Assert.True(content.SequenceEqual(written));
    }

    [Fact]
    public async Task A_server_without_range_support_still_downloads_correctly()
    {
        var content = MakeContent(20 * 1024 * 1024);
        var handler = RangeServer(content, supportRanges: false);

        using var temp = new TempDir();
        var finalPath = temp.File("movie.mkv");
        var sidecar = new PartSidecar { SourceLink = "l" };

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
                (_, _) => Task.FromResult("https://cdn/file"), 8, CancellationToken.None);

            Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        }

        Assert.Single(sidecar.Segments);
        var written = await File.ReadAllBytesAsync(SidecarStore.PartPathFor(finalPath));
        Assert.True(content.SequenceEqual(written));
    }

    [Fact]
    public async Task A_zero_byte_file_completes()
    {
        var handler = RangeServer([]);

        using var temp = new TempDir();
        var finalPath = temp.File("empty.txt");
        var sidecar = new PartSidecar { SourceLink = "l" };

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
                (_, _) => Task.FromResult("https://cdn/file"), 8, CancellationToken.None);

            Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        }
    }

    // -----------------------------------------------------------------------
    // Resume
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Resume_fetches_only_the_missing_bytes()
    {
        var content = MakeContent(20 * 1024 * 1024);
        var handler = RangeServer(content);

        using var temp = new TempDir();
        var finalPath = temp.File("movie.mkv");
        var partPath = SidecarStore.PartPathFor(finalPath);

        // Simulate an interrupted transfer: first half of every segment already on disk.
        var sidecar = new PartSidecar
        {
            SourceLink = "l",
            TotalSize = content.Length,
            SupportsRanges = true,
            ETag = "\"v1\"",
            Segments = SidecarStore.PlanSegments(content.Length, 4)
        };

        await using (var fs = new FileStream(partPath, FileMode.Create))
        {
            fs.SetLength(content.Length);
            foreach (var segment in sidecar.Segments)
            {
                var half = segment.Length / 2;
                fs.Seek(segment.Start, SeekOrigin.Begin);
                await fs.WriteAsync(content.AsMemory((int)segment.Start, (int)half));
                segment.Written = half;
            }
        }

        var alreadyHave = sidecar.DownloadedBytes;
        Assert.True(alreadyHave > 0);

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                partPath, finalPath, sidecar,
                (_, _) => Task.FromResult("https://cdn/file"), 4, CancellationToken.None);

            Assert.Equal(DownloadOutcome.Completed, result.Outcome);

            // Only the missing half was transferred this time.
            Assert.Equal(content.Length - alreadyHave, result.BytesDownloaded);
        }

        var written = await File.ReadAllBytesAsync(partPath);
        Assert.True(content.SequenceEqual(written), "Resumed file does not match the source.");
    }

    [Fact]
    public async Task A_changed_etag_discards_the_partial_and_restarts()
    {
        var content = MakeContent(20 * 1024 * 1024);
        var handler = RangeServer(content, etag: "\"v2\"");   // server now says v2

        using var temp = new TempDir();
        var finalPath = temp.File("movie.mkv");
        var partPath = SidecarStore.PartPathFor(finalPath);

        var sidecar = new PartSidecar
        {
            SourceLink = "l",
            TotalSize = content.Length,
            SupportsRanges = true,
            ETag = "\"v1\"",                                  // we recorded v1
            Segments = SidecarStore.PlanSegments(content.Length, 4)
        };
        sidecar.Segments[0].Written = 1000;

        await File.WriteAllBytesAsync(partPath, new byte[content.Length]);

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                partPath, finalPath, sidecar,
                (_, _) => Task.FromResult("https://cdn/file"), 4, CancellationToken.None);

            Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        }

        // The whole file was refetched, and it matches.
        var written = await File.ReadAllBytesAsync(partPath);
        Assert.True(content.SequenceEqual(written));
    }

    // -----------------------------------------------------------------------
    // Retry and failure classification
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_file_still_completes()
    {
        var content = MakeContent(1000);
        var failures = 0;

        var handler = new FakeHandler((request, n) =>
        {
            if (request.Method == HttpMethod.Head)
                throw new HttpRequestException("no head");

            // Fail the first real data request once, then serve normally.
            if (n == 2 && Interlocked.Increment(ref failures) == 1)
                throw new HttpRequestException("connection reset");

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var from = (int)(range?.From ?? 0);
            var to = (int)(range?.To ?? content.Length - 1);
            to = Math.Min(to, content.Length - 1);

            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[from..(to + 1)])
            };
            partial.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(from, to, content.Length);
            return partial;
        });

        using var temp = new TempDir();
        var finalPath = temp.File("f.bin");
        var sidecar = new PartSidecar { SourceLink = "l" };

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
                (_, _) => Task.FromResult("https://cdn/file"), 1, CancellationToken.None);

            Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        }

        Assert.Equal(1, failures);
        var written = await File.ReadAllBytesAsync(SidecarStore.PartPathFor(finalPath));
        Assert.True(content.SequenceEqual(written));
    }

    [Fact]
    public async Task An_expired_link_is_re_resolved_once()
    {
        var content = MakeContent(1000);
        var resolveCalls = 0;

        var handler = new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                throw new HttpRequestException("no head");

            // The first URL has expired; the refreshed one works.
            if (request.RequestUri!.ToString().Contains("stale"))
                return new HttpResponseMessage(HttpStatusCode.Forbidden);

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var from = (int)(range?.From ?? 0);
            var to = Math.Min((int)(range?.To ?? content.Length - 1), content.Length - 1);

            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[from..(to + 1)])
            };
            partial.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(from, to, content.Length);
            return partial;
        });

        using var temp = new TempDir();
        var finalPath = temp.File("f.bin");
        var sidecar = new PartSidecar { SourceLink = "l" };

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
                (refresh, _) =>
                {
                    resolveCalls++;
                    return Task.FromResult(refresh ? "https://cdn/fresh" : "https://cdn/stale");
                },
                connectionsPerFile: 1,
                CancellationToken.None);

            Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        }

        Assert.True(resolveCalls >= 2, "The resolver should have been asked for a fresh URL.");
    }

    [Fact]
    public async Task A_permanent_http_error_fails_without_exhausting_retries()
    {
        var handler = new FakeHandler((request, _) =>
            request.Method == HttpMethod.Head
                ? throw new HttpRequestException("no head")
                : new HttpResponseMessage(HttpStatusCode.BadRequest));

        using var temp = new TempDir();
        var finalPath = temp.File("f.bin");
        var sidecar = new PartSidecar { SourceLink = "l" };

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
                (_, _) => Task.FromResult("https://cdn/file"), 1, CancellationToken.None);

            Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        }

        // HEAD plus one ranged probe: no retry storm on a 400.
        Assert.True(handler.CallCount <= 3, "Retried a permanent failure: " + handler.CallCount);
    }

    [Fact]
    public async Task A_size_mismatch_is_detected_and_reported()
    {
        // Claims 5000 bytes in the probe but only ever serves 1000.
        var content = MakeContent(1000);

        var handler = new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                head.Content.Headers.ContentLength = 5000;
                head.Headers.AcceptRanges.Add("bytes");
                return head;
            }

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var from = (int)(range?.From ?? 0);
            if (from >= content.Length)
            {
                var empty = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent([])
                };
                empty.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(from, from, 5000);
                return empty;
            }

            var slice = content[from..];
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice)
            };
            partial.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(from, content.Length - 1, 5000);
            return partial;
        });

        using var temp = new TempDir();
        var finalPath = temp.File("f.bin");
        var sidecar = new PartSidecar { SourceLink = "l" };

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
                (_, _) => Task.FromResult("https://cdn/file"), 1, CancellationToken.None);

            // The file is short, so it must not be presented as finished.
            Assert.NotEqual(DownloadOutcome.Completed, result.Outcome);
        }
    }

    [Fact]
    public async Task Cancellation_stops_promptly_and_keeps_the_part_file()
    {
        var content = MakeContent(20 * 1024 * 1024);
        using var cts = new CancellationTokenSource();

        var handler = new FakeHandler((request, n) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                head.Content.Headers.ContentLength = content.Length;
                head.Headers.AcceptRanges.Add("bytes");
                return head;
            }

            cts.Cancel();   // cancel as soon as the data request arrives

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var from = (int)(range?.From ?? 0);
            var to = Math.Min((int)(range?.To ?? content.Length - 1), content.Length - 1);

            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[from..(to + 1)])
            };
            partial.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(from, to, content.Length);
            return partial;
        });

        using var temp = new TempDir();
        var finalPath = temp.File("movie.mkv");
        var sidecar = new PartSidecar { SourceLink = "l" };

        var downloader = MakeDownloader(handler, out var http);
        using (http)
        {
            var result = await downloader.DownloadAsync(
                SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
                (_, _) => Task.FromResult("https://cdn/file"), 4, cts.Token);

            Assert.Equal(DownloadOutcome.Cancelled, result.Outcome);
        }

        // Cancelling must not throw away what was already fetched.
        Assert.True(File.Exists(SidecarStore.PartPathFor(finalPath)));
        Assert.NotNull(SidecarStore.TryLoad(finalPath));
    }
}

public sealed class RateAndFormattingTests
{
    [Fact]
    public void Rate_meter_reports_zero_before_any_samples()
        => Assert.Equal(0, new RateMeter().BytesPerSecond);

    [Fact]
    public void Rate_meter_reports_a_positive_rate_after_samples()
    {
        var meter = new RateMeter(TimeSpan.FromSeconds(5));
        meter.Add(1_000_000);

        Assert.True(meter.BytesPerSecond > 0);
    }

    [Fact]
    public void Rate_meter_resets_to_zero()
    {
        var meter = new RateMeter();
        meter.Add(5000);
        meter.Reset();

        Assert.Equal(0, meter.BytesPerSecond);
    }

    [Fact]
    public async Task Speed_limiter_is_a_no_op_when_disabled()
    {
        var limiter = new SpeedLimiter(0);

        Assert.False(limiter.IsEnabled);
        Assert.Equal(65536, await limiter.AcquireAsync(65536, CancellationToken.None));
    }

    [Fact]
    public async Task Speed_limiter_grants_at_most_what_is_wanted()
    {
        var limiter = new SpeedLimiter(1024);

        var granted = await limiter.AcquireAsync(4096, CancellationToken.None);

        Assert.True(granted is >= 1 and <= 4096);
        Assert.True(limiter.IsEnabled);
    }

    [Fact]
    public async Task Speed_limiter_actually_throttles()
    {
        // 10 KB/s with 30 KB requested must take at least a couple of seconds.
        var limiter = new SpeedLimiter(10 * 1024);
        var start = DateTime.UtcNow;
        var total = 0;

        while (total < 30 * 1024)
            total += await limiter.AcquireAsync(4096, CancellationToken.None);

        Assert.True(DateTime.UtcNow - start > TimeSpan.FromSeconds(1),
            "The limiter did not slow the transfer down.");
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1,023 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(1048576, "1.00 MB")]
    [InlineData(2254857830, "2.10 GB")]
    public void Byte_formatting(long bytes, string expected)
        => Assert.Equal(expected, ByteFormatter.Format(bytes));

    [Fact]
    public void Negative_size_is_an_em_dash()
        => Assert.Equal("—", ByteFormatter.Format(-1));

    [Fact]
    public void Rate_formatting_appends_per_second()
    {
        Assert.Equal("1.00 MB/s", ByteFormatter.FormatRate(1048576));
        Assert.Equal("—", ByteFormatter.FormatRate(0));
        Assert.Equal("—", ByteFormatter.FormatRate(double.NaN));
    }

    [Fact]
    public void Eta_estimation_and_formatting()
    {
        Assert.Equal(TimeSpan.Zero, TimeFormatter.EstimateEta(0, 1000));
        Assert.Null(TimeFormatter.EstimateEta(1000, 0));

        var eta = TimeFormatter.EstimateEta(1000, 100);
        Assert.NotNull(eta);
        Assert.Equal(10, (int)eta.Value.TotalSeconds);

        Assert.Equal("00:00:10", TimeFormatter.FormatEta(TimeSpan.FromSeconds(10)));
        Assert.Equal("00:01:37", TimeFormatter.FormatEta(TimeSpan.FromSeconds(97)));
        Assert.Equal("02:00:00", TimeFormatter.FormatEta(TimeSpan.FromHours(2)));
        Assert.Equal("—", TimeFormatter.FormatEta(null));
        Assert.Equal("—", TimeFormatter.FormatEta(TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Redaction_hides_the_identifying_part_of_a_url()
    {
        var redacted = Redact.Url("https://alldebrid.com/f/1a2b3c4d5e6f");

        Assert.StartsWith("https://alldebrid.com/f/", redacted);
        Assert.DoesNotContain("1a2b3c4d5e6f", redacted);
        Assert.Contains("1a2b", redacted);      // a short hint is still useful in a log
    }

    [Fact]
    public void Redaction_never_reveals_a_secret()
    {
        var redacted = Redact.Secret("TESTKEY0000000000000");

        Assert.DoesNotContain("TESTKEY", redacted);
        Assert.Contains("redacted", redacted);
        Assert.Equal("(none)", Redact.Secret(null));
    }

    [Fact]
    public void Redaction_handles_junk_input()
    {
        Assert.Equal("(none)", Redact.Url(null));
        Assert.Equal("(none)", Redact.Url("  "));
        Assert.Equal("(unparseable url)", Redact.Url("not a url"));
    }
}

public sealed class TransferItemTests
{
    private static TransferItem Make(long size = 1000) => new()
    {
        SourceLink = "https://alldebrid.com/f/x",
        FinalPath = Path.Combine("C:\\D", "Show", "a.mkv"),
        FileName = "a.mkv",
        RelativePath = Path.Combine("Show", "a.mkv"),
        MagnetId = 1,
        TorrentName = "Show",
        ExpectedSize = size
    };

    [Fact]
    public void Progress_arithmetic()
    {
        var item = Make();
        item.TotalBytes = 1000;

        Assert.Equal(0, item.ProgressPercent);

        item.DownloadedBytes = 250;
        Assert.Equal(25, item.ProgressPercent);
        Assert.Equal("25%", item.ProgressText);
        Assert.Equal(750, item.RemainingBytes);

        item.DownloadedBytes = 1000;
        Assert.Equal(100, item.ProgressPercent);
        Assert.Equal(0, item.RemainingBytes);
    }

    [Fact]
    public void Unknown_total_size_shows_bytes_received_instead_of_a_percentage()
    {
        var item = Make(0);
        Assert.False(item.HasKnownSize);
        Assert.Equal("—", item.ProgressText);

        item.DownloadedBytes = 2048;
        Assert.Equal("2.00 KB", item.ProgressText);
        Assert.Equal(0, item.ProgressPercent);
    }

    [Fact]
    public void Progress_never_exceeds_one_hundred_percent()
    {
        var item = Make();
        item.TotalBytes = 1000;
        item.DownloadedBytes = 5000;   // a misbehaving server

        Assert.Equal(100, item.ProgressPercent);
    }

    [Fact]
    public void State_drives_the_available_actions()
    {
        var item = Make();

        item.State = TransferState.Queued;
        Assert.True(item.CanPause);
        Assert.True(item.CanCancel);
        Assert.False(item.CanRetry);
        Assert.False(item.IsFinished);

        item.State = TransferState.Downloading;
        Assert.True(item.IsActive);
        Assert.True(item.CanPause);

        item.State = TransferState.Paused;
        Assert.True(item.CanResume);
        Assert.False(item.IsActive);

        item.State = TransferState.Failed;
        Assert.True(item.CanRetry);
        Assert.True(item.IsFinished);

        item.State = TransferState.Completed;
        Assert.True(item.IsFinished);
        Assert.False(item.CanRetry);
        Assert.False(item.CanPause);
    }

    [Fact]
    public void State_text_reflects_connections_and_retries()
    {
        var item = Make();

        item.State = TransferState.Downloading;
        item.ActiveConnections = 8;
        Assert.Equal("8 conns", item.StateText);

        item.ActiveConnections = 1;
        Assert.Equal("Downloading", item.StateText);

        item.State = TransferState.Retrying;
        item.Attempt = 2;
        Assert.Equal("Retrying (2/5)", item.StateText);

        item.State = TransferState.Completed;
        Assert.Equal("Done", item.StateText);
    }

    [Fact]
    public void A_failed_state_shows_a_truncated_reason()
    {
        var item = Make();
        item.ErrorMessage = new string('x', 200);
        item.State = TransferState.Failed;

        Assert.StartsWith("Failed: ", item.StateText);
        Assert.True(item.StateText.Length < 100);
    }

    [Fact]
    public void Reset_for_retry_clears_the_error_and_requeues()
    {
        var item = Make();
        item.ErrorMessage = "boom";
        item.TechnicalDetails = "detail";
        item.Attempt = 3;
        item.State = TransferState.Failed;

        item.ResetForRetry();

        Assert.Null(item.ErrorMessage);
        Assert.Null(item.TechnicalDetails);
        Assert.Equal(0, item.Attempt);
        Assert.Equal(TransferState.Queued, item.State);
    }

    [Fact]
    public void Reported_bytes_accumulate_across_threads()
    {
        var item = Make(100_000);

        Parallel.For(0, 100, _ => item.ReportBytes(1000));

        Assert.Equal(100_000, item.DownloadedBytes);
    }

    [Fact]
    public void Part_path_derives_from_the_final_path()
        => Assert.Equal(Make().FinalPath + ".part", Make().PartPath);

    [Fact]
    public void Speed_and_eta_are_dashes_when_not_downloading()
    {
        var item = Make();
        item.State = TransferState.Queued;

        Assert.Equal("—", item.SpeedText);
        Assert.Equal("—", item.EtaText);
        Assert.Equal(0, item.BytesPerSecond);
    }
}
