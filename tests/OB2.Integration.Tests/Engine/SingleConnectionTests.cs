using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Integrity;
using OB2.Integration.Tests.Infrastructure;

namespace OB2.Integration.Tests.Engine;

/// <summary>Phase 2: single-connection engine against the local test server.</summary>
public sealed class SingleConnectionTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private EngineFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        _fx = new EngineFixture();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Downloads_file_byte_for_byte()
    {
        const long size = 3 * 1024 * 1024 + 17;

        var item = await _fx.DownloadAsync(_server.FileUrl(size, "data.bin"));

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        item.DestPath.Should().Be(Path.Combine(_fx.SaveFolder, "data.bin"));
        item.TotalBytes.Should().Be(size);
        item.DownloadedBytes.Should().Be(size);
        item.CompletedAt.Should().NotBeNull();
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        File.Exists(item.PartPath).Should().BeFalse("the .pgpart file is renamed on completion");
        _fx.Repository.GetHistory(item.Id).Select(e => e.EventType).Should().ContainInOrder("added", "started", "completed");
    }

    [Fact]
    public async Task Probe_reads_size_name_and_range_support()
    {
        var probe = await _fx.Engine.ProbeAsync(new DownloadRequest { Url = _server.FileUrl(1000, "a b.iso"), SaveFolder = _fx.SaveFolder });

        probe.TotalBytes.Should().Be(1000);
        probe.SupportsRanges.Should().BeTrue();
        probe.SuggestedFileName.Should().Be("a b.iso");
        probe.ETag.Should().Be("\"v1-1000\"");
    }

    [Fact]
    public async Task Head_refused_falls_back_to_ranged_get()
    {
        var probe = await _fx.Engine.ProbeAsync(new DownloadRequest { Url = _server.FileUrl(5000, query: "headStatus=405"), SaveFolder = _fx.SaveFolder });

        probe.TotalBytes.Should().Be(5000);
        probe.SupportsRanges.Should().BeTrue();
        _server.Requests.Should().Contain(r => r.Method == "GET" && r.Header("Range") == "bytes=0-0");
    }

    [Fact]
    public async Task Redirect_is_followed_and_final_url_recorded()
    {
        var target = "/file/4096/real.bin";

        var item = await _fx.DownloadAsync(_server.Url($"/redirect?to={Uri.EscapeDataString(target)}"));

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        item.FinalUrl.Should().EndWith(target);
        item.FileName.Should().Be("real.bin");
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(4096));
    }

    [Fact]
    public async Task Content_disposition_name_wins_including_unicode()
    {
        var item = await _fx.DownloadAsync(_server.FileUrl(100, "ignored.bin", "disposition=" + Uri.EscapeDataString("تقرير 2026.pdf")));

        item.FileName.Should().Be("تقرير 2026.pdf");
        File.Exists(Path.Combine(_fx.SaveFolder, "تقرير 2026.pdf")).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"..\..\evil.exe", "evil.exe")]
    [InlineData("../../../Windows/System32/evil.dll", "evil.dll")]
    [InlineData("CON", "_CON")]
    [InlineData("..", "download")]
    public async Task Path_traversal_in_server_name_stays_inside_save_folder(string hostile, string expected)
    {
        // AC-11
        var item = await _fx.DownloadAsync(_server.FileUrl(64, query: "disposition=" + Uri.EscapeDataString(hostile)));

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        item.DestPath.Should().Be(Path.Combine(_fx.SaveFolder, expected));
        Directory.GetFiles(_fx.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(_fx.SaveFolder, StringComparison.OrdinalIgnoreCase) && !f.Contains("ob2.db", StringComparison.Ordinal))
            .Should().BeEmpty("nothing may be written outside the save folder");
    }

    [Fact]
    public async Task Name_collision_is_renamed()
    {
        File.WriteAllText(Path.Combine(_fx.SaveFolder, "setup.exe"), "existing");

        var first = await _fx.DownloadAsync(_server.FileUrl(10, "setup.exe"));
        var second = await _fx.DownloadAsync(_server.FileUrl(10, "setup.exe"));

        first.FileName.Should().Be("setup (1).exe");
        second.FileName.Should().Be("setup (2).exe");
        File.ReadAllText(Path.Combine(_fx.SaveFolder, "setup.exe")).Should().Be("existing");
    }

    [Fact]
    public async Task Sends_cookies_referrer_user_agent_and_custom_headers()
    {
        var item = await _fx.DownloadAsync(
            _server.FileUrl(2048, "private.zip", "cookie=session%3Dabc123"),
            cookies: "session=abc123; theme=dark",
            referrer: "https://example.com/downloads",
            userAgent: "OB2-Test/1.0",
            headers: new Dictionary<string, string> { ["Accept-Language"] = "ar-IQ", ["Authorization"] = "Bearer secret" });

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        var get = _server.Requests.Last(r => r.Method == "GET");
        get.Header("Cookie").Should().Be("session=abc123; theme=dark");
        get.Header("Referer").Should().Be("https://example.com/downloads");
        get.Header("User-Agent").Should().Be("OB2-Test/1.0");
        get.Header("Accept-Language").Should().Be("ar-IQ");
        get.Header("Accept-Encoding").Should().Be("identity");

        // Credentials never reach the database (§11.1).
        var stored = _fx.Repository.Get(item.Id)!;
        stored.Headers.Should().ContainKey("Accept-Language").And.NotContainKey("Authorization");
    }

    [Fact]
    public async Task Missing_cookie_fails_with_http_403()
    {
        var request = new DownloadRequest { Url = _server.FileUrl(10, query: "cookie=session%3Dabc"), SaveFolder = _fx.SaveFolder };

        var act = () => _fx.Engine.AddAsync(request);

        (await act.Should().ThrowAsync<OB2.Core.Http.HttpStatusException>()).Which.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Server_without_ranges_completes_on_one_connection()
    {
        const long size = 700_000;

        var item = await _fx.DownloadAsync(_server.FileUrl(size, query: "norange=1"));

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        item.SupportsRanges.Should().BeFalse();
        item.Connections.Should().Be(1);
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
    }

    [Fact]
    public async Task Unknown_length_completes_and_records_size()
    {
        const long size = 500_000;

        var item = await _fx.DownloadAsync(_server.FileUrl(size, query: "nolength=1"));

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        item.TotalBytes.Should().Be(size);
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
    }

    [Fact]
    public async Task Zero_byte_file_completes()
    {
        var item = await _fx.DownloadAsync(_server.FileUrl(0, "empty.txt"));

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        new FileInfo(item.DestPath).Length.Should().Be(0);
    }

    [Fact]
    public async Task Matching_sha256_and_md5_complete()
    {
        const long size = 200_000;
        Checksum.TryCreate("sha256", TestHttpServer.Sha256Of(size), out var sha).Should().BeTrue();
        Checksum.TryCreate("md5", TestHttpServer.Md5Of(size), out var md5).Should().BeTrue();

        var a = await _fx.DownloadAsync(_server.FileUrl(size, "a.bin"), sha);
        var b = await _fx.DownloadAsync(_server.FileUrl(size, "b.bin"), md5);

        a.Status.Should().Be(DownloadStatus.Completed, a.ErrorMessage);
        b.Status.Should().Be(DownloadStatus.Completed, b.ErrorMessage);
        _fx.Repository.GetHistory(a.Id).Should().Contain(e => e.EventType == "verified");
    }

    [Fact]
    public async Task Sha256_mismatch_fails_and_keeps_the_partial_file()
    {
        Checksum.TryCreate("sha256", new string('A', 64), out var wrong).Should().BeTrue();

        var item = await _fx.DownloadAsync(_server.FileUrl(50_000, "iso.bin"), wrong);

        item.Status.Should().Be(DownloadStatus.Failed);
        item.ErrorCode.Should().Be(ErrorCodes.ChecksumMismatch);
        File.Exists(item.PartPath).Should().BeTrue("the .pgpart file is kept for inspection");
        new FileInfo(item.PartPath).Length.Should().Be(50_000);
        File.Exists(item.DestPath).Should().BeFalse("a file that fails verification is never renamed");
    }

    [Fact]
    public async Task Not_found_fails_with_http_code()
    {
        // The file existed when added and is gone by the time the download starts.
        var item = await _fx.AddAsync(_server.FileUrl(10_000, "gone.bin"));
        item.Url = _server.Url("/status/404").AbsoluteUri;
        _fx.Repository.Update(item);

        var result = await _fx.RunAsync(item.Id);

        result.Status.Should().Be(DownloadStatus.Failed);
        result.ErrorCode.Should().Be("HTTP_404");
        result.ErrorMessage.Should().Contain("404");
    }

    [Fact]
    public async Task Pause_then_reopen_resumes_from_checkpoint_without_corruption()
    {
        // AC-2, single connection: pause, close the app, reopen, resume.
        const long size = 4 * 1024 * 1024;
        var item = await _fx.AddAsync(_server.FileUrl(size, "big.bin", "bps=2000000"));
        _fx.Engine.Start(item.Id);
        await _fx.WaitForCheckpointAsync(item.Id, 1024 * 1024);

        await _fx.Engine.PauseAsync(item.Id);
        var paused = _fx.Repository.Get(item.Id)!;
        paused.Status.Should().Be(DownloadStatus.Paused);
        paused.DownloadedBytes.Should().BeInRange(1024 * 1024, size - 1);
        File.Exists(paused.PartPath).Should().BeTrue();

        _fx.Restart();
        var resumed = await _fx.RunAsync(item.Id);

        resumed.Status.Should().Be(DownloadStatus.Completed, resumed.ErrorMessage);
        EngineFixture.Sha256OfFile(resumed.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        _server.Requests.Should().Contain(r => r.Method == "GET" && r.Header("Range") == $"bytes={paused.DownloadedBytes}-{size - 1}",
            "the resume must continue from the checkpoint, not restart");
        _fx.Repository.GetHistory(item.Id).Select(e => e.EventType).Should().ContainInOrder("started", "paused", "resumed", "completed");
    }

    [Fact]
    public async Task Changed_remote_file_restarts_instead_of_mixing_versions()
    {
        const long size = 2 * 1024 * 1024;
        var item = await _fx.AddAsync(_server.FileUrl(size, "v.bin", "bps=2000000"));
        _fx.Engine.Start(item.Id);
        await _fx.WaitForCheckpointAsync(item.Id, 512 * 1024);
        await _fx.Engine.PauseAsync(item.Id);

        // Same URL, new ETag: the server now has a different file.
        var stored = _fx.Repository.Get(item.Id)!;
        stored.Url = _server.FileUrl(size, "v.bin", "etag=v2").AbsoluteUri;
        _fx.Repository.Update(stored);

        var result = await _fx.RunAsync(item.Id);

        result.Status.Should().Be(DownloadStatus.Completed, result.ErrorMessage);
        _server.Requests.Last(r => r.Method == "GET" && r.PathAndQuery.Contains("etag=v2", StringComparison.Ordinal))
            .Header("Range").Should().Be($"bytes=0-{size - 1}");
    }

    [Fact]
    public async Task Startup_recovery_marks_interrupted_downloads_paused()
    {
        var item = await _fx.AddAsync(_server.FileUrl(10, "x.bin"));
        item.Status = DownloadStatus.Downloading;
        _fx.Repository.Update(item);

        _fx.Restart();

        _fx.Repository.Get(item.Id)!.Status.Should().Be(DownloadStatus.Paused);
    }
}
