using System.Diagnostics;
using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Files;
using OB2.Core.Integrity;
using OB2.Integration.Tests.Infrastructure;
using Xunit.Abstractions;

namespace OB2.Integration.Tests.Engine;

/// <summary>Phase 3: segmented downloads, retries, timeouts, throttling, and rate limiting.</summary>
public sealed class MultiConnectionTests : IAsyncLifetime
{
    private const int MB = 1024 * 1024;

    private readonly ITestOutputHelper _output;
    private TestHttpServer _server = null!;
    private EngineFixture _fx = null!;

    public MultiConnectionTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        _fx = new EngineFixture(EngineFixture.FastOptions with { Connections = 8, MinSegmentBytes = 256 * 1024 });
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Eight_connections_download_byte_for_byte()
    {
        const long size = 32L * MB + 12345;

        var item = await _fx.DownloadAsync(_server.FileUrl(size, "eight.bin"), connections: 8);

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        item.Connections.Should().Be(8);
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        var segments = _fx.Repository.GetSegments(item.Id);
        segments.Should().HaveCount(8).And.OnlyContain(s => s.Status == SegmentStatus.Done);
        segments.Sum(s => s.Length!.Value).Should().Be(size);
        _server.MaxConcurrentResponses.Should().BeGreaterThan(1, "segments must download in parallel");
        PartFile.IsSparse(item.DestPath).Should().BeFalse("the sparse flag is cleared on completion");
    }

    [Fact]
    public async Task Partial_file_is_sparse_while_segments_download()
    {
        var item = await _fx.AddAsync(_server.FileUrl(16L * MB, "sparse.bin", "bps=4000000"), connections: 8);
        _fx.Engine.Start(item.Id);
        await _fx.WaitForCheckpointAsync(item.Id, 1);

        PartFile.IsSparse(item.PartPath).Should().BeTrue();
        await _fx.Engine.PauseAsync(item.Id);
    }

    [LargeFact]
    public async Task AC1_one_gigabyte_with_eight_connections_matches_checksum()
    {
        const long size = 1024L * MB;
        _fx.Options = new EngineOptions { Connections = 8, ReadIdleTimeout = TimeSpan.FromSeconds(30) };
        _fx.Restart();
        Checksum.TryCreate("sha256", TestHttpServer.Sha256Of(size), out var expected).Should().BeTrue();

        var watch = Stopwatch.StartNew();
        var item = await _fx.DownloadAsync(_server.FileUrl(size, "one-gig.bin"), expected, connections: 8);
        watch.Stop();

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        item.Connections.Should().Be(8);
        _fx.Repository.GetHistory(item.Id).Should().Contain(e => e.EventType == "verified");
        _output.WriteLine($"AC-1: 1 GiB, 8 connections, {watch.Elapsed.TotalSeconds:0.0} s incl. SHA-256 verify, {size / watch.Elapsed.TotalSeconds / MB:0.0} MiB/s");
    }

    [Fact]
    public async Task AC3_five_dropped_connections_in_one_segment_are_retried()
    {
        const long size = 8L * MB;
        var lastSegmentStart = 7L * MB;

        var item = await _fx.DownloadAsync(
            _server.FileUrl(size, "flaky.bin", $"dropAfter=100000&drops=5&dropFrom={lastSegmentStart}"),
            connections: 8);

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        _server.Requests.Count(r => r.Method == "GET" && RangeStart(r) >= lastSegmentStart).Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public async Task AC3_five_drops_without_progress_still_complete_on_the_sixth_attempt()
    {
        const long size = 2L * MB;

        var item = await _fx.DownloadAsync(_server.FileUrl(size, "zero.bin", "dropAfter=0&drops=5"), connections: 1);

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        _server.Requests.Count(r => r.Method == "GET").Should().Be(6);
    }

    [Fact]
    public async Task Exhausted_retries_fail_keep_progress_and_can_be_retried()
    {
        // Flow C: retries run out → Failed with detail → the user presses Retry.
        const long size = 4L * MB;
        // One connection: 1 attempt + 5 retries (default) all hit the 6 drops.
        var item = await _fx.AddAsync(_server.FileUrl(size, "stubborn.bin", "dropAfter=0&drops=6"), connections: 1);

        var failed = await _fx.RunAsync(item.Id);

        failed.Status.Should().Be(DownloadStatus.Failed);
        failed.ErrorCode.Should().Be(ErrorCodes.Network);
        failed.ErrorMessage.Should().NotBeNullOrEmpty();
        File.Exists(failed.PartPath).Should().BeTrue();

        var retried = await _fx.RunAsync(item.Id);

        retried.Status.Should().Be(DownloadStatus.Completed, retried.ErrorMessage);
        EngineFixture.Sha256OfFile(retried.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
    }

    [Fact]
    public async Task AC4_server_without_ranges_uses_one_connection_even_when_eight_are_requested()
    {
        const long size = 3L * MB;

        var item = await _fx.DownloadAsync(_server.FileUrl(size, "single.bin", "norange=1"), connections: 8);

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        item.Connections.Should().Be(1);
        _fx.Repository.GetSegments(item.Id).Should().ContainSingle();
        _server.MaxConcurrentResponses.Should().Be(1);
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
    }

    [Fact]
    public async Task AC2_multi_connection_pause_reopen_resume_has_no_corrupted_bytes()
    {
        const long size = 12L * MB;
        var item = await _fx.AddAsync(_server.FileUrl(size, "resume8.bin", "bps=500000"), connections: 8);
        _fx.Engine.Start(item.Id);
        await _fx.WaitForCheckpointAsync(item.Id, 2L * MB);
        await _fx.Engine.PauseAsync(item.Id);

        var segmentsAtPause = _fx.Repository.GetSegments(item.Id);
        segmentsAtPause.Should().HaveCount(8);
        segmentsAtPause.Count(s => s.Downloaded > 0).Should().BeGreaterThan(1);
        var requestsBefore = _server.Requests.Count;

        _fx.Restart();
        var resumed = await _fx.RunAsync(item.Id);

        resumed.Status.Should().Be(DownloadStatus.Completed, resumed.ErrorMessage);
        EngineFixture.Sha256OfFile(resumed.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        var resumeRanges = _server.Requests.Skip(requestsBefore).Where(r => r.Method == "GET").Select(RangeStart).ToList();
        foreach (var segment in segmentsAtPause.Where(s => s.Downloaded > 0 && !s.IsComplete))
        {
            resumeRanges.Should().Contain(segment.Start + segment.Downloaded, $"segment {segment.Index} resumes from its checkpoint");
        }
    }

    [Fact]
    public async Task Server_that_ignores_ranges_after_advertising_them_falls_back_to_one_connection()
    {
        const long size = 5L * MB;

        var item = await _fx.DownloadAsync(_server.FileUrl(size, "liar.bin", "rangeLie=1"), connections: 8);

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        item.SupportsRanges.Should().BeFalse();
        item.Connections.Should().Be(1);
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
    }

    [Fact]
    public async Task Transient_503s_are_retried()
    {
        const long size = 1L * MB;

        var item = await _fx.DownloadAsync(_server.FileUrl(size, "busy.bin", "fail=503&failCount=3"), connections: 1);

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        _fx.Repository.GetHistory(item.Id).Should().Contain(e => e.EventType == "completed");
    }

    [Fact]
    public async Task Permanent_errors_are_not_retried()
    {
        var item = await _fx.AddAsync(_server.FileUrl(1000, "x.bin"));
        item.Url = _server.Url("/status/410").AbsoluteUri;
        _fx.Repository.Update(item);

        var result = await _fx.RunAsync(item.Id);

        result.ErrorCode.Should().Be("HTTP_410");
        _server.Requests.Count(r => r.PathAndQuery == "/status/410" && r.Method == "GET").Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Stalled_connection_hits_the_idle_timeout_and_is_retried()
    {
        _fx.Options = _fx.Options with { ReadIdleTimeout = TimeSpan.FromSeconds(1) };
        const long size = 1L * MB;

        var item = await _fx.DownloadAsync(_server.FileUrl(size, "stall.bin", "stallAfter=100000&stalls=1"), connections: 1);

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        _server.Requests.Should().Contain(r => r.Method == "GET" && RangeStart(r) >= 100_000, "the retry continues after the stalled bytes");
    }

    [Fact]
    public async Task Per_host_cap_limits_parallel_connections()
    {
        _fx.Options = _fx.Options with { MaxConnectionsPerHost = 3 };
        _fx.Restart();

        var item = await _fx.DownloadAsync(_server.FileUrl(8L * MB, "capped.bin", "bps=4000000"), connections: 8);

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        _server.MaxConcurrentResponses.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task Server_refusing_extra_connections_makes_the_engine_back_off()
    {
        // §16.1: a server that allows only two connections at a time.
        const long size = 8L * MB;

        var item = await _fx.DownloadAsync(_server.FileUrl(size, "polite.bin", "maxConcurrent=2&bps=8000000"), connections: 8);

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        _server.RejectedResponses.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Global_rate_limit_paces_the_download()
    {
        const long size = 3L * MB;
        _fx.Engine.GlobalLimiter.BytesPerSecond = 1L * MB;
        var watch = Stopwatch.StartNew();

        var item = await _fx.DownloadAsync(_server.FileUrl(size, "slow.bin"), connections: 4);

        watch.Stop();
        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        // 3 MiB at 1 MiB/s with up to a 1 MiB burst: at least ~2 s.
        watch.Elapsed.TotalSeconds.Should().BeGreaterThan(1.8);
        _output.WriteLine($"3 MiB at a 1 MiB/s limit took {watch.Elapsed.TotalSeconds:0.00} s");
    }

    [Fact]
    public async Task Resume_reconciles_segments_against_a_truncated_partial_file()
    {
        // §11.3: crash recovery must never trust bytes that are not on disk.
        const long size = 8L * MB;
        var item = await _fx.AddAsync(_server.FileUrl(size, "trunc.bin", "bps=1000000"), connections: 8);
        _fx.Engine.Start(item.Id);
        await _fx.WaitForCheckpointAsync(item.Id, 2L * MB);
        await _fx.Engine.PauseAsync(item.Id);

        using (var file = new FileStream(item.PartPath, FileMode.Open))
        {
            file.SetLength(1L * MB + 7);
        }

        _fx.Restart();
        var result = await _fx.RunAsync(item.Id);

        result.Status.Should().Be(DownloadStatus.Completed, result.ErrorMessage);
        EngineFixture.Sha256OfFile(result.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
    }

    [LargeFact]
    public async Task Benchmark_throughput()
    {
        // Phase 3 exit criterion: throughput recorded against the local test server.
        _fx.Options = new EngineOptions { ReadIdleTimeout = TimeSpan.FromSeconds(30) };
        _fx.Restart();

        // A server that caps each connection at 4 MB/s, like many CDNs and mirrors.
        foreach (var connections in new[] { 1, 4, 8, 16 })
        {
            const long size = 64L * MB;
            var watch = Stopwatch.StartNew();
            var item = await _fx.DownloadAsync(_server.FileUrl(size, $"bench-{connections}.bin", "bps=4000000"), connections: connections);
            watch.Stop();
            item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
            _output.WriteLine($"per-connection cap 4 MB/s, 64 MiB, {connections,2} connection(s): {size / watch.Elapsed.TotalSeconds / MB,6:0.0} MiB/s");
        }

        // Unthrottled loopback: measures the engine itself (CPU, disk, checkpoints).
        foreach (var connections in new[] { 1, 8 })
        {
            const long size = 512L * MB;
            var watch = Stopwatch.StartNew();
            var item = await _fx.DownloadAsync(_server.FileUrl(size, $"raw-{connections}.bin"), connections: connections);
            watch.Stop();
            item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
            _output.WriteLine($"unthrottled loopback, 512 MiB, {connections,2} connection(s): {size / watch.Elapsed.TotalSeconds / MB,6:0.0} MiB/s");
        }
    }

    private static long RangeStart(RecordedRequest request)
    {
        var range = request.Header("Range");
        if (range is null || !range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            return 0;
        }

        return long.Parse(range[6..range.IndexOf('-', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
    }
}
