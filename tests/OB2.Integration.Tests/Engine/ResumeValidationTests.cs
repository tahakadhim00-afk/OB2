using OB2.Core.Downloads;
using OB2.Core.Files;
using OB2.Integration.Tests.Infrastructure;

namespace OB2.Integration.Tests.Engine;

/// <summary>
/// §10.3: a server offering neither ETag nor Last-Modified gives a resume nothing to check but
/// the bytes themselves. Without the head fingerprint a file replaced by one of the same length
/// resumes onto different bytes and the result is silently corrupt.
/// </summary>
public sealed class ResumeValidationTests : IAsyncLifetime
{
    private const int MB = 1024 * 1024;

    private TestHttpServer _server = null!;
    private EngineFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        _fx = new EngineFixture(EngineFixture.FastOptions with { Connections = 1 });
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Pausing_records_a_fingerprint_of_the_first_bytes()
    {
        var item = await PauseAfterAsync(4L * MB, "fp.bin", "novalidator=1");

        var saved = _fx.Repository.Get(item.Id)!;
        saved.HeadSha256.Should().NotBeNullOrEmpty();
        saved.HeadBytes.Should().Be(HeadFingerprint.Length);
    }

    [Fact]
    public async Task Resume_starts_over_when_the_bytes_changed_behind_an_unchanged_length()
    {
        // Paused against variant 0, resumed against variant 1: same length, no validators,
        // different bytes. Only the fingerprint can tell, and the file must come out whole.
        var item = await PauseAfterAsync(4L * MB, "swap.bin", "novalidator=1");
        var downloadedAtPause = _fx.Repository.Get(item.Id)!.DownloadedBytes;
        downloadedAtPause.Should().BeGreaterThan(0);

        _fx.Repository.Get(item.Id)!.Url.Should().Contain("variant=0");
        Retarget(item.Id, "variant=1");

        var finished = await _fx.RunAsync(item.Id);

        finished.Status.Should().Be(DownloadStatus.Completed, finished.ErrorMessage);
        EngineFixture.Sha256OfFile(finished.DestPath).Should().Be(
            Sha256OfVariant(4L * MB, 1),
            "the changed file was downloaded from the start rather than stitched onto stale bytes");
    }

    [Fact]
    public async Task Resume_keeps_progress_when_the_bytes_are_unchanged()
    {
        // The counterpart: an identical file must still resume, not restart, or the check would
        // cost every no-validator download its progress.
        var item = await PauseAfterAsync(4L * MB, "same.bin", "novalidator=1");
        var atPause = _fx.Repository.Get(item.Id)!.DownloadedBytes;

        var finished = await _fx.RunAsync(item.Id);

        finished.Status.Should().Be(DownloadStatus.Completed, finished.ErrorMessage);
        EngineFixture.Sha256OfFile(finished.DestPath).Should().Be(Sha256OfVariant(4L * MB, 0));
        _fx.Repository.GetHistory(item.Id).Should().Contain(e => e.EventType == "resumed",
            "an unchanged file resumes rather than starting again");
        atPause.Should().BeGreaterThan(0);
    }

    private async Task<DownloadItem> PauseAfterAsync(long size, string name, string query)
    {
        // Slow enough to pause mid-transfer, but past the fingerprint's 64 KB.
        var url = _server.FileUrl(size, name, query + "&variant=0&bps=900000");
        var item = await _fx.AddAsync(url, connections: 1);
        _fx.Engine.Start(item.Id);
        await _fx.WaitForCheckpointAsync(item.Id, HeadFingerprint.Length + 1);
        await _fx.Engine.PauseAsync(item.Id);
        return item;
    }

    /// <summary>Points the stored download at a different variant of the same-length file.</summary>
    private void Retarget(long id, string variant)
    {
        var item = _fx.Repository.Get(id)!;
        item.Url = item.Url.Replace("variant=0", variant, StringComparison.Ordinal);
        item.FinalUrl = item.FinalUrl?.Replace("variant=0", variant, StringComparison.Ordinal);
        _fx.Repository.Update(item);
    }

    private static string Sha256OfVariant(long size, int variant)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        for (long offset = 0; offset < size; offset += buffer.Length)
        {
            var n = (int)Math.Min(buffer.Length, size - offset);
            TestHttpServer.Fill(buffer.AsSpan(0, n), offset, variant);
            hash.AppendData(buffer, 0, n);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
