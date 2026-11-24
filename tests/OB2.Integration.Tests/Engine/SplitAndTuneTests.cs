using System.Diagnostics;
using OB2.Core.Downloads;
using OB2.Integration.Tests.Infrastructure;
using Xunit.Abstractions;

namespace OB2.Integration.Tests.Engine;

/// <summary>
/// D-90: every connection busy to the end (idle connections take over half of the biggest
/// unfinished segment) and more connections while they make it faster. Every file is compared
/// with the server's bytes; every saved set of segments must cover the file exactly once.
/// These tests measure speed, so they run alone (see <see cref="SpeedMeasuringSerialGroup"/>).
/// </summary>
[Collection("Speed measuring")]
public sealed class SplitAndTuneTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const long MB = 1024 * 1024;

    private TestHttpServer _server = null!;
    private EngineFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        _fixture = new EngineFixture();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _server.DisposeAsync();
    }

    private void Use(bool split, bool auto = false, int perHost = 16)
    {
        _fixture.Options = EngineFixture.FastOptions with
        {
            SplitSegments = split,
            AutoConnections = auto,
            MaxConnectionsPerHost = perHost,
            // Long enough that the test server's 64 KB chunks do not make each reading jumpy.
            TuneInterval = TimeSpan.FromSeconds(1),
        };
        _fixture.Rebuild();
    }

    /// <summary>The saved segments cover [0, size) once, in order, and none counts bytes past its end.</summary>
    private static void ShouldCoverExactly(IReadOnlyList<Segment> segments, long size)
    {
        var ordered = segments.OrderBy(s => s.Start).ToList();
        ordered[0].Start.Should().Be(0);
        ordered[^1].End.Should().Be(size - 1);
        for (var i = 1; i < ordered.Count; i++)
        {
            ordered[i].Start.Should().Be(ordered[i - 1].End + 1, "segment {0} must follow segment {1} with no gap or overlap", ordered[i].Index, ordered[i - 1].Index);
        }

        segments.Should().OnlyContain(s => s.Downloaded >= 0 && s.Downloaded <= s.Length);
    }

    [Fact]
    public async Task One_slow_connection_no_longer_holds_up_the_whole_download()
    {
        // 4 connections at 2 MB/s, except the one for the first part at 250 KB/s: without
        // splitting the download waits about 4 s for that part; with it, idle connections take
        // over the rest of it.
        const long size = 4 * MB;
        Use(split: false);
        var watch = Stopwatch.StartNew();
        var before = await _fixture.DownloadAsync(_server.FileUrl(size, "before.bin", "bps=2000000&slowFirst=250000"), connections: 4);
        var unsplit = watch.Elapsed;

        Use(split: true);
        watch.Restart();
        var after = await _fixture.DownloadAsync(_server.FileUrl(size, "after.bin", "bps=2000000&slowFirst=250000"), connections: 4);
        var split = watch.Elapsed;

        before.Status.Should().Be(DownloadStatus.Completed, before.ErrorMessage);
        after.Status.Should().Be(DownloadStatus.Completed, after.ErrorMessage);
        EngineFixture.Sha256OfFile(before.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        EngineFixture.Sha256OfFile(after.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        var segments = _fixture.Repository.GetSegments(after.Id);
        output.WriteLine($"4 MiB, 4 connections, one at 250 KB/s: {unsplit.TotalSeconds:0.00} s without splitting, {split.TotalSeconds:0.00} s with it ({segments.Count} segments)");
        segments.Count.Should().BeGreaterThan(4, "idle connections split the slow part");
        ShouldCoverExactly(segments, size);
        split.Should().BeLessThan(unsplit / 2, $"without splitting {unsplit.TotalSeconds:0.0} s, with it {split.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task A_resumed_download_uses_all_its_connections_again()
    {
        // First run without splitting: three parts finish, the slow first one does not. Paused
        // there, a resume used to continue on one connection, one per unfinished part.
        const long size = 8 * MB;
        Use(split: false);
        var url = _server.FileUrl(size, "resume.bin", "bps=1000000&slowFirst=100000");
        var item = await _fixture.AddAsync(url, connections: 4);
        _fixture.Engine.Start(item.Id).Should().BeTrue();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_fixture.Engine.GetProgress(item.Id) is not { } progress || progress.Segments.Count(s => s.IsComplete) < 3)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "three parts finish in about 2 s");
            await Task.Delay(50);
        }

        await _fixture.Engine.PauseAsync(item.Id);
        _fixture.Repository.GetSegments(item.Id).Count(s => !s.IsComplete).Should().Be(1);

        Use(split: true);
        _server.ResetConcurrency();
        var done = await _fixture.RunAsync(item.Id);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        EngineFixture.Sha256OfFile(done.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        _server.MaxConcurrentResponses.Should().BeGreaterThanOrEqualTo(3, "the one unfinished part is shared by all four connections");
        ShouldCoverExactly(_fixture.Repository.GetSegments(item.Id), size);
    }

    [Fact]
    public async Task A_pause_after_splits_saves_segments_that_cover_the_file_and_the_resume_finishes_it()
    {
        const long size = 16 * MB;
        Use(split: true);
        var item = await _fixture.AddAsync(_server.FileUrl(size, "pause.bin", "bps=1500000&slowFirst=150000"), connections: 4);
        _fixture.Engine.Start(item.Id).Should().BeTrue();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_fixture.Engine.GetProgress(item.Id) is not { } progress || progress.Segments.Count < 7)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "splits start once the fast parts finish");
            await Task.Delay(50);
        }

        await _fixture.Engine.PauseAsync(item.Id);
        var saved = _fixture.Repository.GetSegments(item.Id);
        saved.Count.Should().BeGreaterThanOrEqualTo(7);
        ShouldCoverExactly(saved, size);
        _fixture.Repository.Get(item.Id)!.DownloadedBytes.Should().Be(saved.Sum(s => s.Downloaded), "the progress saved is the sum of the segments saved");

        var done = await _fixture.RunAsync(item.Id);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        EngineFixture.Sha256OfFile(done.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        var final = _fixture.Repository.GetSegments(item.Id);
        ShouldCoverExactly(final, size);
        final.Sum(s => s.Downloaded).Should().Be(size);
    }

    [Fact]
    public async Task A_server_that_limits_each_connection_gets_more_connections()
    {
        // 1 MB/s per connection and no other limit: starting at 2, OB2 should keep adding.
        const long size = 24 * MB;
        var (logs, capture) = LogCapture.Create();
        _fixture.LoggerFactory = logs;
        Use(split: true, auto: true, perHost: 8);

        var done = await _fixture.DownloadAsync(_server.FileUrl(size, "grow.bin", "bps=1000000"), connections: 2);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        EngineFixture.Sha256OfFile(done.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        var tuned = capture.Lines.Where(l => l.Contains("connections", StringComparison.Ordinal) && l.Contains("->", StringComparison.Ordinal)).ToList();
        output.WriteLine(string.Join(Environment.NewLine, tuned));
        tuned.Should().Contain(l => l.Contains("2 -> 4", StringComparison.Ordinal), string.Join(Environment.NewLine, tuned));
        _server.MaxConcurrentResponses.Should().BeGreaterThanOrEqualTo(6, string.Join(Environment.NewLine, tuned));
        ShouldCoverExactly(_fixture.Repository.GetSegments(done.Id), size);
    }

    [Fact]
    public async Task When_the_line_is_full_the_extra_connections_are_given_back()
    {
        // 1 MB/s per connection, but 3.5 MB/s for all together (a full line): 2 -> 4 helps
        // (+75 %, 50 % asked), 4 -> 6 cannot (0 %, 25 % asked), so it goes back to 4 and stops.
        const long size = 32 * MB;
        var (logs, capture) = LogCapture.Create();
        _fixture.LoggerFactory = logs;
        Use(split: true, auto: true, perHost: 16);

        var done = await _fixture.DownloadAsync(_server.FileUrl(size, "full.bin", "bps=1000000&totalBps=3500000"), connections: 2);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        EngineFixture.Sha256OfFile(done.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        var tuned = Tuned(capture);
        output.WriteLine(string.Join(Environment.NewLine, tuned));
        string.Join(" | ", tuned).Should().ContainAll("2 -> 4", "4 -> 6", "6 -> 4");
        tuned.Count(l => l.Contains("4 -> 6", StringComparison.Ordinal)).Should().Be(
            tuned.Count(l => l.Contains("6 -> 4", StringComparison.Ordinal)),
            "D-91: it may try again later, but every try on a full line is undone: " + string.Join(" | ", tuned));
    }

    [Fact]
    public async Task A_speed_limit_the_download_has_reached_keeps_the_connections_as_they_are()
    {
        // Two connections could bring 2 MB/s; the limit lets 1.5 through, so it decides the speed.
        const long size = 6 * MB;
        var (logs, capture) = LogCapture.Create();
        _fixture.LoggerFactory = logs;
        Use(split: true, auto: true);
        _fixture.Engine.GlobalLimiter.BytesPerSecond = 1_500_000;

        var done = await _fixture.DownloadAsync(_server.FileUrl(size, "limited.bin", "bps=1000000"), connections: 2);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        EngineFixture.Sha256OfFile(done.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        Tuned(capture).Should().BeEmpty("a speed limit the download has reached decides the speed, not the connections");
    }

    [Fact]
    public async Task A_speed_limit_far_above_the_speed_does_not_stop_more_connections()
    {
        // D-91: the owner's 50,000 KB/s cap kept every download on its first 8 connections,
        // though the servers gave 0.3 MB/s per connection and the downloads ran at 2.4 MB/s.
        const long size = 24 * MB;
        var (logs, capture) = LogCapture.Create();
        _fixture.LoggerFactory = logs;
        Use(split: true, auto: true, perHost: 8);
        _fixture.Engine.GlobalLimiter.BytesPerSecond = 51_200_000;

        var done = await _fixture.DownloadAsync(_server.FileUrl(size, "far-limit.bin", "bps=1000000"), connections: 2);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        EngineFixture.Sha256OfFile(done.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        var tuned = Tuned(capture);
        output.WriteLine(string.Join(Environment.NewLine, tuned));
        tuned.Should().Contain(l => l.Contains("2 -> 4", StringComparison.Ordinal), string.Join(Environment.NewLine, tuned));
    }

    [Fact]
    public async Task A_resumed_download_starts_with_the_connections_it_had_reached()
    {
        // D-91: a resume used to start again from the configured number and climb for a while.
        const long size = 64 * MB;
        var (logs, capture) = LogCapture.Create();
        _fixture.LoggerFactory = logs;
        Use(split: true, auto: true, perHost: 8);
        var item = await _fixture.AddAsync(_server.FileUrl(size, "reached.bin", "bps=1000000"), connections: 2);
        _fixture.Engine.Start(item.Id).Should().BeTrue();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!Tuned(capture).Any(l => l.Contains("4 -> 6", StringComparison.Ordinal)))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "at 1 MB/s per connection, 2 -> 4 -> 6 takes about 4 s");
            await Task.Delay(50);
        }

        await _fixture.Engine.PauseAsync(item.Id);
        var reached = _fixture.Repository.Get(item.Id)!.Connections;
        output.WriteLine($"Remembered at the pause: {reached} connections; {string.Join(" | ", Tuned(capture))}");
        reached.Should().BeGreaterThanOrEqualTo(4, "4 had been shown to help before the pause (6 maybe not yet)");

        var done = await _fixture.RunAsync(item.Id);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        EngineFixture.Sha256OfFile(done.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        ShouldCoverExactly(_fixture.Repository.GetSegments(item.Id), size);
        capture.Lines.Should().Contain(
            l => l.Contains($"{reached} connection(s), resume=True", StringComparison.Ordinal),
            string.Join(Environment.NewLine, capture.Lines.Where(l => l.Contains("started", StringComparison.Ordinal))));
    }

    /// <summary>The log lines of connections added or given back.</summary>
    private static List<string> Tuned(LogCapture capture) =>
        [.. capture.Lines.Where(l => l.Contains("connections", StringComparison.Ordinal) && l.Contains("->", StringComparison.Ordinal))];
}

/// <summary>
/// Tests that decide on measured speed run after all the others, one at a time. Alongside the
/// rest of the suite this process (and the test server inside it) sometimes stalls for a moment:
/// one 1-second reading came out at 0.0 MB/s, another at 3.5 MB/s on two connections capped at
/// 1 MB/s each, and the tuner rightly went back on what it measured. Alone they passed 30 of 30.
/// </summary>
[CollectionDefinition("Speed measuring", DisableParallelization = true)]
public sealed class SpeedMeasuringSerialGroup;
