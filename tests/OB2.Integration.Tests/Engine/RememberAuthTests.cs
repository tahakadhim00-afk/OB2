using Microsoft.Data.Sqlite;
using OB2.Core.Downloads;
using OB2.Integration.Tests.Infrastructure;
using OB2.Storage.Repositories;

namespace OB2.Integration.Tests.Engine;

/// <summary>
/// §11.1 "remember auth" (decision D-67): with the option on, an authenticated download resumes
/// after the app restarts; with it off, cookies stay in memory only.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RememberAuthTests : IAsyncLifetime
{
    // The server requires "session=ab12" (named in the URL); "token=REMEMBER-ME-123" never appears
    // in any URL, so finding it anywhere on disk would mean the cookie leaked.
    private const string RequiredCookie = "session=ab12";
    private const string Cookie = RequiredCookie + "; token=REMEMBER-ME-123";

    private TestHttpServer _server = null!;
    private EngineFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        _fx = new EngineFixture();
        _fx.CookieStore = new DpapiCookieStore(_fx.ConnectionFactory);
        _fx.Rebuild();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task With_the_option_on_a_signed_in_download_resumes_after_a_restart()
    {
        _fx.Options = _fx.Options with { RememberAuth = true };
        const long size = 3_000_000;
        var item = await StartAndPauseAsync(size);

        _fx.Restart(); // new engine: the in-memory cookie map is gone
        var result = await _fx.RunAsync(item.Id);

        result.Status.Should().Be(DownloadStatus.Completed, result.ErrorMessage);
        EngineFixture.Sha256OfFile(result.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        _fx.CookieStore!.Load(item.Id).Should().BeNull("a finished download forgets its cookies");
    }

    [Fact]
    public async Task With_the_option_off_cookies_are_not_kept_across_a_restart()
    {
        var item = await StartAndPauseAsync(3_000_000);

        _fx.CookieStore!.Load(item.Id).Should().BeNull();
        _fx.Restart();
        var result = await _fx.RunAsync(item.Id);

        result.Status.Should().Be(DownloadStatus.Failed, "the server needs the cookie, which only lived in memory");
        result.ErrorCode.Should().Be("HTTP_403");
    }

    [Fact]
    public async Task Turning_the_option_on_saves_cookies_already_in_memory_and_off_deletes_them()
    {
        var item = await StartAndPauseAsync(3_000_000);
        _fx.CookieStore!.Load(item.Id).Should().BeNull();

        _fx.Engine.SyncSavedCookies(remember: true);
        _fx.CookieStore.Load(item.Id).Should().Be(Cookie);

        _fx.Engine.SyncSavedCookies(remember: false);
        _fx.CookieStore.Load(item.Id).Should().BeNull();
    }

    [Fact]
    public async Task The_cookie_value_is_never_stored_in_plain_text()
    {
        _fx.Options = _fx.Options with { RememberAuth = true };
        await StartAndPauseAsync(3_000_000);
        _fx.Engine.Dispose();
        SqliteConnection.ClearAllPools();

        foreach (var file in Directory.GetFiles(_fx.Root, "ob2.db*"))
        {
            File.ReadAllText(file).Should().NotContain("REMEMBER-ME", file);
        }

        _fx.Rebuild();
    }

    private async Task<DownloadItem> StartAndPauseAsync(long size)
    {
        var url = _server.FileUrl(size, "private.bin", "bps=500000&cookie=" + Uri.EscapeDataString(RequiredCookie));
        var item = await _fx.AddAsync(url, cookies: Cookie);
        _fx.Engine.Start(item.Id);
        await _fx.WaitForCheckpointAsync(item.Id, 300_000);
        await _fx.Engine.PauseAsync(item.Id);
        return item;
    }
}
