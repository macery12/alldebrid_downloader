using System.Net.Http.Headers;
using AllDebridDownloader.Models;
using AllDebridDownloader.Services;

namespace AllDebridDownloader.Tests;

/// <summary>
/// The failure modes that separate a robust downloader from a plain GET: a stalled but
/// open connection, a blip before the first byte, an already-expired link, and a
/// truncated transfer that must not be reported as finished.
/// </summary>
public sealed class RobustnessTests : IDisposable
{
    private readonly TestLogger _log = new();

    public void Dispose() => _log.Dispose();

    /// <summary>A stream that yields some bytes then hangs forever.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly int _bytesBeforeStall;
        private int _delivered;

        public StallingStream(int bytesBeforeStall) => _bytesBeforeStall = bytesBeforeStall;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_delivered < _bytesBeforeStall)
            {
                var n = Math.Min(buffer.Length, _bytesBeforeStall - _delivered);
                buffer.Span[..n].Fill(1);
                _delivered += n;
                return n;
            }

            // Open, but never delivers anything again.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_stalled_but_open_connection_is_detected_and_the_transfer_fails()
    {
        const int total = 10_000;

        var handler = new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                head.Content.Headers.ContentLength = total;
                head.Headers.AcceptRanges.Add("bytes");
                return head;
            }

            // Delivers 100 bytes then goes quiet without closing.
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new StallingStream(100))
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, total - 1, total);
            return response;
        });

        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new SegmentedFileDownloader(http, _log.Logger, new SpeedLimiter(0))
        {
            StallTimeout = TimeSpan.FromMilliseconds(300),
            MaxAttemptsPerSegment = 2
        };

        using var temp = new TempDir();
        var finalPath = temp.File("stalled.bin");
        var sidecar = new PartSidecar { SourceLink = "l" };

        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await downloader.DownloadAsync(
            SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
            (_, _) => Task.FromResult("https://cdn/file"),
            connectionsPerFile: 1,
            giveUp.Token);

        // It must give up rather than hang forever on a connection that is open but dead.
        Assert.False(giveUp.IsCancellationRequested,
            "The stall detector never fired; the download hung.");
        Assert.Equal(DownloadOutcome.Failed, result.Outcome);

        // And the partial bytes are kept for a later resume.
        Assert.True(File.Exists(SidecarStore.PartPathFor(finalPath)));
    }

    [Fact]
    public async Task A_blip_before_the_first_byte_is_retried_rather_than_failing_the_file()
    {
        var content = new byte[2000];
        Random.Shared.NextBytes(content);
        var probeAttempts = 0;

        var handler = new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                throw new HttpRequestException("HEAD refused");

            // The first two probe attempts fail outright, the third succeeds.
            if (request.Headers.Range?.Ranges.FirstOrDefault() is { From: 0, To: 0 })
            {
                if (++probeAttempts < 3)
                    throw new HttpRequestException("connection reset during probe");

                var probeOk = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(content[..1])
                };
                probeOk.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(0, 0, content.Length);
                return probeOk;
            }

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

        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new SegmentedFileDownloader(http, _log.Logger, new SpeedLimiter(0));

        using var temp = new TempDir();
        var finalPath = temp.File("f.bin");
        var sidecar = new PartSidecar { SourceLink = "l" };

        var result = await downloader.DownloadAsync(
            SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
            (_, _) => Task.FromResult("https://cdn/file"), 1, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(3, probeAttempts);

        var written = await File.ReadAllBytesAsync(SidecarStore.PartPathFor(finalPath));
        Assert.True(content.SequenceEqual(written));
    }

    [Fact]
    public async Task An_already_expired_link_is_re_resolved_at_probe_time()
    {
        var content = new byte[1500];
        Random.Shared.NextBytes(content);
        var sawFreshUrl = false;

        var handler = new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                throw new HttpRequestException("HEAD refused");

            // The stale URL is already dead before a single byte was fetched.
            if (request.RequestUri!.ToString().Contains("stale"))
                return new HttpResponseMessage(HttpStatusCode.Gone);

            sawFreshUrl = true;

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

        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new SegmentedFileDownloader(http, _log.Logger, new SpeedLimiter(0));

        using var temp = new TempDir();
        var finalPath = temp.File("f.bin");
        var sidecar = new PartSidecar { SourceLink = "l" };

        var result = await downloader.DownloadAsync(
            SidecarStore.PartPathFor(finalPath), finalPath, sidecar,
            (refresh, _) => Task.FromResult(refresh ? "https://cdn/fresh" : "https://cdn/stale"),
            1, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(sawFreshUrl, "The expired link was never re-resolved.");
    }

    [Fact]
    public async Task A_truncated_transfer_is_never_reported_as_complete()
    {
        // The server advertises 5000 bytes but only ever delivers 1000. Because the
        // .part file is pre-allocated to 5000, its size on disk cannot reveal this --
        // the check has to count bytes actually received.
        var content = new byte[1000];
        Random.Shared.NextBytes(content);

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
                empty.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, from, 5000);
                return empty;
            }

            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[from..])
            };
            partial.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(from, content.Length - 1, 5000);
            return partial;
        });

        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new SegmentedFileDownloader(http, _log.Logger, new SpeedLimiter(0));

        using var temp = new TempDir();
        var finalPath = temp.File("short.bin");
        var partPath = SidecarStore.PartPathFor(finalPath);
        var sidecar = new PartSidecar { SourceLink = "l" };

        var result = await downloader.DownloadAsync(
            partPath, finalPath, sidecar,
            (_, _) => Task.FromResult("https://cdn/file"), 1, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("Size mismatch", result.ErrorMessage);

        // The pre-allocated file is the full advertised length, proving the check could
        // not have relied on it.
        Assert.Equal(5000, new FileInfo(partPath).Length);
        Assert.Equal(1000, sidecar.DownloadedBytes);
    }

    [Fact]
    public async Task A_416_on_the_probe_is_treated_as_an_empty_file()
    {
        var handler = new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                throw new HttpRequestException("HEAD refused");

            // A correct server answers 416 to a 0-0 range on an empty file.
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Content = new ByteArrayContent([]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0);
            return response;
        });

        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new SegmentedFileDownloader(http, _log.Logger, new SpeedLimiter(0));

        var info = await downloader.ProbeAsync("https://cdn/empty", CancellationToken.None);

        Assert.Equal(0, info.ContentLength);
        Assert.False(info.SupportsRanges);
    }

    [Fact]
    public async Task An_authoritative_zero_length_head_skips_the_range_probe()
    {
        var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);   // no GET should follow

            var head = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
            head.Content.Headers.ContentLength = 0;
            head.Headers.AcceptRanges.Add("bytes");
            return head;
        });

        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new SegmentedFileDownloader(http, _log.Logger, new SpeedLimiter(0));

        var info = await downloader.ProbeAsync("https://cdn/empty", CancellationToken.None);

        Assert.Equal(0, info.ContentLength);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Interrupted_downloads_are_discovered_by_their_sidecars()
    {
        using var temp = new TempDir();

        var torrentDir = Path.Combine(temp.Path, "Some.Show.S01");
        Directory.CreateDirectory(torrentDir);

        // One resumable file, one already complete, one sidecar with no .part beside it.
        var resumable = Path.Combine(torrentDir, "S01E01.mkv");
        await File.WriteAllBytesAsync(SidecarStore.PartPathFor(resumable), new byte[500]);
        var resumableSidecar = new PartSidecar
        {
            SourceLink = "https://alldebrid.com/f/a",
            TorrentName = "Some.Show.S01",
            MagnetId = 7,
            TotalSize = 1000,
            Segments = SidecarStore.PlanSegments(1000, 1)
        };
        resumableSidecar.Segments[0].Written = 500;   // matches the 500 bytes on disk
        await SidecarStore.SaveAsync(resumable, resumableSidecar);

        var finished = Path.Combine(torrentDir, "S01E02.mkv");
        var finishedSidecar = new PartSidecar
        {
            SourceLink = "https://alldebrid.com/f/b",
            TotalSize = 1000,
            Segments = SidecarStore.PlanSegments(1000, 1)
        };
        finishedSidecar.Segments[0].Written = 1000;
        await File.WriteAllBytesAsync(SidecarStore.PartPathFor(finished), new byte[1000]);
        await SidecarStore.SaveAsync(finished, finishedSidecar);

        var orphanSidecar = Path.Combine(torrentDir, "S01E03.mkv");
        await SidecarStore.SaveAsync(orphanSidecar, new PartSidecar
        {
            SourceLink = "https://alldebrid.com/f/c",
            TotalSize = 1000,
            Segments = SidecarStore.PlanSegments(1000, 1)
        });

        var found = DownloadManager.FindInterrupted(temp.Path, _log.Logger);

        // Only the genuinely resumable one: not the complete one, not the orphan.
        Assert.Single(found);
        Assert.Equal(resumable, found[0].FinalPath);
        Assert.Equal(500, found[0].Sidecar.DownloadedBytes);
    }

    [Fact]
    public async Task A_discovered_sidecar_converts_back_into_a_queueable_transfer()
    {
        using var temp = new TempDir();
        var torrentDir = Path.Combine(temp.Path, "Show");
        Directory.CreateDirectory(torrentDir);

        var finalPath = Path.Combine(torrentDir, "Season 1", "a.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await File.WriteAllBytesAsync(SidecarStore.PartPathFor(finalPath), new byte[10]);

        var sidecar = new PartSidecar
        {
            SourceLink = "https://alldebrid.com/f/a",
            TorrentName = "Show",
            MagnetId = 42,
            TotalSize = 1000,
            Segments = SidecarStore.PlanSegments(1000, 1)
        };

        var item = DownloadManager.ToTransfer(finalPath, sidecar, temp.Path);

        Assert.NotNull(item);
        Assert.Equal("https://alldebrid.com/f/a", item.SourceLink);
        Assert.Equal(42, item.MagnetId);
        Assert.Equal("a.mkv", item.FileName);
        Assert.Equal(Path.Combine("Show", "Season 1", "a.mkv"), item.RelativePath);
        Assert.Equal(1000, item.ExpectedSize);
    }

    [Fact]
    public void A_sidecar_with_no_source_link_cannot_be_resumed()
    {
        // Without the original /f/ link there is nothing to re-unlock, so the file has to
        // restart rather than be silently queued as resumable.
        var sidecar = new PartSidecar { TotalSize = 1000 };

        Assert.Null(DownloadManager.ToTransfer("C:\\D\\a.mkv", sidecar, "C:\\D"));
    }

    [Fact]
    public void Free_space_is_reported_for_a_real_drive()
    {
        var free = DownloadManager.GetFreeSpace(Path.GetTempPath());

        Assert.NotNull(free);
        Assert.True(free > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-rooted-path")]
    [InlineData("ZZ:\\nope")]
    public void Free_space_is_null_for_a_path_it_cannot_resolve(string path)
    {
        // A relative path must not be resolved against the current directory: that would
        // report a different drive's free space and wave through a batch that cannot fit.
        Assert.Null(DownloadManager.GetFreeSpace(path));
    }
}

public sealed class ConfigServiceTests
{
    [Fact]
    public void A_missing_config_starts_from_defaults()
    {
        using var temp = new TempDir();
        var service = new ConfigService(null, temp.Path);

        var config = service.Load();

        Assert.Equal(3, config.MaxConcurrentFiles);
        Assert.Equal(8, config.ConnectionsPerFile);
        Assert.Equal(3, config.StatusPollIntervalSeconds);
        Assert.True(config.ResumeOnStartup);
        Assert.Null(config.ApiKey);
    }

    [Fact]
    public async Task Config_round_trips_and_never_leaves_a_temp_file()
    {
        using var temp = new TempDir();
        var service = new ConfigService(null, temp.Path);
        service.Load();

        service.Current.ApiKey = "TESTKEY0000000000000";
        service.Current.DownloadDirectory = temp.Path;
        service.Current.MaxConcurrentFiles = 5;
        await service.SaveAsync();

        Assert.False(File.Exists(service.ConfigPath + ".tmp"));

        var reloaded = new ConfigService(null, temp.Path).Load();
        Assert.Equal("TESTKEY0000000000000", reloaded.ApiKey);
        Assert.Equal(5, reloaded.MaxConcurrentFiles);
    }

    [Fact]
    public void A_corrupt_config_is_quarantined_and_defaults_are_used()
    {
        using var temp = new TempDir();
        var service = new ConfigService(null, temp.Path);

        File.WriteAllText(service.ConfigPath, "{ this is not json ");

        var config = service.Load();

        Assert.Equal(3, config.MaxConcurrentFiles);
        Assert.NotEmpty(Directory.GetFiles(temp.Path, "config.bad-*.json"));
        Assert.False(File.Exists(service.ConfigPath));
    }

    [Fact]
    public void Out_of_range_values_are_clamped_on_load()
    {
        using var temp = new TempDir();
        var service = new ConfigService(null, temp.Path);

        File.WriteAllText(service.ConfigPath, """
        {"schemaVersion":1,"maxConcurrentFiles":99,"connectionsPerFile":0,
         "statusPollIntervalSeconds":1,"globalSpeedLimitBytesPerSecond":-5}
        """);

        var config = service.Load();

        Assert.Equal(8, config.MaxConcurrentFiles);
        Assert.Equal(1, config.ConnectionsPerFile);
        Assert.Equal(2, config.StatusPollIntervalSeconds);
        Assert.Equal(0, config.GlobalSpeedLimitBytesPerSecond);
    }

    [Fact]
    public void A_writable_folder_is_accepted_and_a_bad_drive_is_not()
    {
        using var temp = new TempDir();

        Assert.True(ConfigService.IsUsableDownloadDirectory(temp.Path, out var problem));
        Assert.Null(problem);

        Assert.False(ConfigService.IsUsableDownloadDirectory("ZZ:\\nope", out var badDrive));
        Assert.NotNull(badDrive);

        Assert.False(ConfigService.IsUsableDownloadDirectory("not-a-rooted-path", out var relative));
        Assert.Contains("full path", relative);

        Assert.False(ConfigService.IsUsableDownloadDirectory("", out var empty));
        Assert.NotNull(empty);
    }

    [Fact]
    public void A_folder_that_does_not_exist_yet_is_created()
    {
        using var temp = new TempDir();
        var nested = Path.Combine(temp.Path, "a", "b", "c");

        Assert.True(ConfigService.IsUsableDownloadDirectory(nested, out _));
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public void The_write_probe_leaves_nothing_behind()
    {
        using var temp = new TempDir();

        ConfigService.IsUsableDownloadDirectory(temp.Path, out _);

        Assert.Empty(Directory.GetFiles(temp.Path, ".adw-write-test-*"));
    }
}

public sealed class MagnetValidationTests
{
    [Theory]
    [InlineData("magnet:?xt=urn:btih:842783e3005495d5d1637f5364b59343c7844707")]
    [InlineData("magnet:?xt=urn:btih:842783e3005495d5d1637f5364b59343c7844707&dn=ubuntu.iso")]
    [InlineData("MAGNET:?XT=URN:BTIH:842783E3005495D5D1637F5364B59343C7844707")]
    [InlineData("842783e3005495d5d1637f5364b59343c7844707")]
    [InlineData("  842783e3005495d5d1637f5364b59343c7844707  ")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567")]
    public void Valid_magnets_and_hashes_are_accepted(string input)
        => Assert.True(ViewModels.MainViewModel.LooksLikeMagnet(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a magnet")]
    [InlineData("http://example.com/file.torrent")]
    [InlineData("magnet:?dn=no-hash-here")]
    [InlineData("842783e3")]
    [InlineData("842783e3005495d5d1637f5364b59343c784470")]
    public void Invalid_input_is_rejected(string input)
        => Assert.False(ViewModels.MainViewModel.LooksLikeMagnet(input));
}
