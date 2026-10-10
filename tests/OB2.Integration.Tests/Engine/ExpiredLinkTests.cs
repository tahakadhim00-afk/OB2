using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Integration.Tests.Infrastructure;

namespace OB2.Integration.Tests.Engine;

/// <summary>
/// §16.1 / decision D-66: a signed CDN address that expires mid-download is replaced by asking
/// the original link again, instead of failing the download.
/// </summary>
public sealed class ExpiredLinkTests : IAsyncLifetime
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
    public async Task An_expired_signed_url_is_refreshed_and_the_download_finishes()
    {
        // Each signed link allows 2 GETs. Both segments drop after 200 KB, so their retries hit
        // the expired link (403) and need a fresh one from /sign.
        const long size = 4_000_000;
        var url = _server.Url($"/sign/{size}/expiring.bin?uses=2&dropAfter=200000&drops=2");

        var item = await _fx.DownloadAsync(url, connections: 2);

        item.Status.Should().Be(DownloadStatus.Completed, item.ErrorMessage);
        EngineFixture.Sha256OfFile(item.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        _fx.Repository.GetHistory(item.Id).Select(e => e.EventType).Should().Contain("url-refreshed");
        // Add probe (t1), job start probe (t2), then one refresh shared by both segments (t3).
        SignRequests().Should().Be(3);
        item.FinalUrl.Should().Contain("token=t3");
    }

    [Fact]
    public async Task A_refused_address_that_does_not_change_still_fails_and_keeps_progress()
    {
        // No redirect: asking the original link again gives the same address, so the 403 is real.
        const long size = 4_000_000;
        var url = _server.FileUrl(size, "fixed.bin", "token=fixed&uses=2&dropAfter=200000&drops=2");

        var item = await _fx.DownloadAsync(url, connections: 2);

        item.Status.Should().Be(DownloadStatus.Failed);
        item.ErrorCode.Should().Be("HTTP_403");
        File.Exists(item.PartPath).Should().BeTrue("progress is kept for a later retry");
        _fx.Repository.GetHistory(item.Id).Select(e => e.EventType).Should().NotContain("url-refreshed");
    }

    [Fact]
    public async Task A_link_that_keeps_expiring_gives_up_after_three_refreshes()
    {
        var url = _server.Url("/sign/1000000/never.bin?uses=0");

        var item = await _fx.DownloadAsync(url);

        item.Status.Should().Be(DownloadStatus.Failed);
        item.ErrorCode.Should().Be("HTTP_403");
        _fx.Repository.GetHistory(item.Id).Count(e => e.EventType == "url-refreshed").Should().Be(DownloadEngine.MaxUrlRefreshes);
        SignRequests().Should().Be(2 + DownloadEngine.MaxUrlRefreshes);
    }

    [Fact]
    public async Task A_link_that_now_serves_a_different_file_is_not_spliced_in()
    {
        // The refreshed link answers with another ETag: mixing its bytes in would corrupt the file.
        var url = _server.Url("/sign/2000000/changed.bin?uses=1&dropAfter=100000&drops=1&rotateEtag=1");

        var item = await _fx.DownloadAsync(url);

        item.Status.Should().Be(DownloadStatus.Failed);
        item.ErrorCode.Should().Be("HTTP_403");
        _fx.Repository.GetHistory(item.Id).Select(e => e.EventType).Should().NotContain("url-refreshed");
        SignRequests().Should().Be(3, "add, start, and the one refresh that found a different file");
    }

    private int SignRequests() => _server.Requests.Count(r => r.PathAndQuery.StartsWith("/sign/", StringComparison.Ordinal));
}
