using Microsoft.Data.Sqlite;
using OB2.Core.Downloads;
using OB2.Integration.Tests.Infrastructure;
using OB2.Storage.Database;
using OB2.Storage.Repositories;
using OB2.Tests.Common;

namespace OB2.Integration.Tests.Cli;

/// <summary>Phase 2 CLI: <c>ob2.exe get &lt;url&gt; [--sha256 &lt;hex&gt;] [--out &lt;dir&gt;]</c>.</summary>
[Collection("OB2.exe")]
public sealed class CliTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly TempDirectory _temp = new();
    private TestHttpServer _server = null!;

    private string Out => _temp.Combine("out");

    private string Data => _temp.Combine("data");

    public async Task InitializeAsync() => _server = await TestHttpServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public async Task Get_downloads_and_verifies_sha256()
    {
        const long size = 1_500_000;

        var (exit, output) = await Ob2Process.RunAsync(Timeout, "get", _server.FileUrl(size, "tool.zip").AbsoluteUri,
            "--out", Out, "--data", Data, "--sha256", TestHttpServer.Sha256Of(size).ToLowerInvariant());

        exit.Should().Be(0, output);
        output.Should().Contain("Saved");
        EngineFixture.Sha256OfFile(Path.Combine(Out, "tool.zip")).Should().Be(TestHttpServer.Sha256Of(size));
    }

    [Fact]
    public async Task Get_with_wrong_sha256_exits_2_and_keeps_partial_file()
    {
        var (exit, output) = await Ob2Process.RunAsync(Timeout, "get", _server.FileUrl(10_000, "x.iso").AbsoluteUri,
            "--out", Out, "--data", Data, "--sha256", new string('0', 64));

        exit.Should().Be(2, output);
        output.Should().Contain("CHECKSUM_MISMATCH");
        File.Exists(Path.Combine(Out, "x.iso.pgpart")).Should().BeTrue();
        File.Exists(Path.Combine(Out, "x.iso")).Should().BeFalse();
    }

    [Theory]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("not a url")]
    public async Task Bad_url_is_a_usage_error(string url)
    {
        var (exit, output) = await Ob2Process.RunAsync(Timeout, "get", url, "--data", Data);

        exit.Should().Be(64, output);
        output.Should().Contain("Usage:");
    }

    [Fact]
    public async Task Killed_process_resumes_from_checkpoint_without_corruption()
    {
        // Crash simulation (Phase 2 exit criterion): kill OB2.exe mid-download, run again.
        // 4 connections × 250 KB/s ≈ 1 MB/s, so 16 MiB takes ~16 s: time for the 5-second checkpoint.
        const long size = 16 * 1024 * 1024;
        var url = _server.FileUrl(size, "crash.bin", "bps=250000").AbsoluteUri;

        using (var process = Ob2Process.Start("get", url, "--out", Out, "--data", Data, "--connections", "4"))
        {
            // The CLI checkpoints every 5 seconds (§11.3); wait for one, then kill without warning.
            await WaitForCheckpointAsync(minBytes: 1);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var killedAt = ReadDownload().DownloadedBytes;
        killedAt.Should().BeInRange(1, size - 1);
        File.Exists(Path.Combine(Out, "crash.bin.pgpart")).Should().BeTrue();

        var (exit, output) = await Ob2Process.RunAsync(Timeout, "get", url, "--out", Out, "--data", Data, "--connections", "4");

        exit.Should().Be(0, output);
        output.Should().Contain("Resuming");
        EngineFixture.Sha256OfFile(Path.Combine(Out, "crash.bin")).Should().Be(TestHttpServer.Sha256Of(size));
        _server.Requests.Where(r => r.Method == "GET" && r.Header("Range") is { } range && !range.StartsWith("bytes=0-", StringComparison.Ordinal))
            .Should().NotBeEmpty("the second run must continue from the checkpoint");
        ReadDownload().Status.Should().Be(DownloadStatus.Completed);
    }

    private DownloadItem ReadDownload()
    {
        var repository = new SqliteDownloadRepository(new SqliteConnectionFactory(Path.Combine(Data, "data", "ob2.db")));
        return repository.List().Single();
    }

    private async Task WaitForCheckpointAsync(long minBytes)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(Path.Combine(Data, "data", "ob2.db")))
            {
                try
                {
                    if (ReadDownload() is { DownloadedBytes: var bytes } && bytes >= minBytes)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
                {
                    // Database still being created by the child process.
                }
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("OB2.exe never wrote a checkpoint.");
    }
}

[CollectionDefinition("OB2.exe", DisableParallelization = true)]
public sealed class Ob2ExeSerialGroup;
