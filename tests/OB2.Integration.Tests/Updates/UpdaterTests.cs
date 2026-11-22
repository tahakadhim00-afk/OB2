using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OB2.Core.Updates;
using OB2.Integration.Tests.Infrastructure;
using OB2.Tests.Common;

namespace OB2.Integration.Tests.Updates;

/// <summary>
/// D-84, D-85: checking a release feed and downloading an update from a local stand-in for GitHub:
/// the release JSON, the zip, its checksum file and the signature, all under <c>/static/</c>.
/// </summary>
public sealed class UpdaterTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly ECDsa _developer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private TestHttpServer _server = null!;

    private string Feed => _temp.Combine("feed");

    public async Task InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        Directory.CreateDirectory(Feed);
        _server.StaticFolder = Feed;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _developer.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task The_checker_finds_a_newer_release_and_sends_only_its_version()
    {
        Publish("1.2.0", signedBy: _developer);

        var release = await Checker().CheckAsync(new Version(1, 1, 0));

        release!.Version.Should().Be(new Version(1, 2, 0));
        release.CanInstall.Should().BeTrue();
        var request = _server.Requests.Single(r => r.PathAndQuery == "/static/latest.json");
        request.Header("User-Agent").Should().Be("OB2/1.1.0");
        request.Header("Cookie").Should().BeNull();
    }

    [Fact]
    public async Task No_release_published_yet_is_up_to_date_not_an_error() =>
        (await Checker().CheckAsync(new Version(1, 1, 0))).Should().BeNull();

    [Fact]
    public async Task An_unreachable_feed_is_a_readable_error()
    {
        var checker = new UpdateChecker(new HttpClient(), new Uri("http://127.0.0.1:1/latest.json"));

        var act = () => checker.CheckAsync(new Version(1, 1, 0));

        await act.Should().ThrowAsync<UpdateException>().WithMessage("*could not reach GitHub*");
    }

    [Fact]
    public async Task A_signed_release_is_downloaded_checked_and_unpacked()
    {
        Publish("1.2.0", signedBy: _developer);
        var release = (await Checker().CheckAsync(new Version(1, 1, 0)))!;
        using var downloader = Downloader();
        var reports = new List<UpdateDownloadProgress>();

        var prepared = await downloader.DownloadAsync(release, new SyncProgress<UpdateDownloadProgress>(reports.Add));

        prepared.Version.Should().Be(new Version(1, 2, 0));
        File.ReadAllText(Path.Combine(prepared.Folder, "OB2.exe")).Should().Be("OB2 1.2.0");
        File.ReadAllText(Path.Combine(prepared.Folder, "chrome-extension", "content.js")).Should().Be("button with a close");
        reports.Should().NotBeEmpty();
        Directory.EnumerateFiles(_temp.Combine("updates", "downloads"), "*.zip").Should().BeEmpty("the zip is deleted once unpacked");
    }

    [Fact]
    public async Task A_release_signed_by_anyone_else_is_refused_before_its_zip_is_fetched()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish("1.2.0", signedBy: attacker);
        var release = (await Checker().CheckAsync(new Version(1, 1, 0)))!;
        using var downloader = Downloader();

        var act = () => downloader.DownloadAsync(release, null);

        await act.Should().ThrowAsync<UpdateException>().WithMessage("*not signed by OB2's developer*");
        _server.Requests.Should().NotContain(r => r.PathAndQuery.EndsWith(".zip", StringComparison.Ordinal));
        Directory.Exists(_temp.Combine("updates", "1.2.0")).Should().BeFalse();
    }

    [Fact]
    public async Task A_zip_changed_after_signing_is_refused()
    {
        Publish("1.2.0", signedBy: _developer);
        File.AppendAllText(Path.Combine(Feed, UpdateChecker.ZipAsset), "tampered");
        var release = (await Checker().CheckAsync(new Version(1, 1, 0)))!;
        using var downloader = Downloader();

        var act = () => downloader.DownloadAsync(release, null);

        await act.Should().ThrowAsync<UpdateException>().WithMessage("*does not match its checksum*");
        Directory.Exists(_temp.Combine("updates", "1.2.0")).Should().BeFalse();
    }

    [Fact]
    public async Task A_release_whose_OB2_exe_is_another_version_is_refused()
    {
        // The real OB2.exe this build made says 1.1.0; a release tagged 9.9.9 must not install it,
        // or the updated copy would offer the same update for ever.
        Publish("9.9.9", signedBy: _developer, appExe: Ob2Process.ExePath);
        var release = (await Checker().CheckAsync(new Version(1, 1, 0)))!;
        using var downloader = Downloader();

        var act = () => downloader.DownloadAsync(release, null);

        await act.Should().ThrowAsync<UpdateException>().WithMessage("*is version 1.1.0, not 9.9.9*");
    }

    [Fact]
    public async Task A_build_without_a_release_key_installs_nothing()
    {
        Publish("1.2.0", signedBy: _developer);
        var release = (await Checker().CheckAsync(new Version(1, 1, 0)))!;
        using var downloader = new UpdateDownloader(_temp.Combine("updates"), trustedKeys: []);

        var act = () => downloader.DownloadAsync(release, null);

        downloader.HasTrustedKey.Should().BeFalse();
        await act.Should().ThrowAsync<UpdateException>().WithMessage("*no release key*");
    }

    private UpdateChecker Checker() => new(new HttpClient(), _server.Url("/static/latest.json"));

    private UpdateDownloader Downloader() => new(_temp.Combine("updates"), [ReleaseSignature.PublicKeyOf(_developer)]);

    /// <summary>What build/publish.ps1 -GitHub puts on a release, served from the feed folder.</summary>
    private void Publish(string version, ECDsa signedBy, string? appExe = null)
    {
        var staging = _temp.Combine("staging-" + version);
        Directory.CreateDirectory(Path.Combine(staging, "chrome-extension"));
        if (appExe is null)
        {
            File.WriteAllText(Path.Combine(staging, "OB2.exe"), $"OB2 {version}");
        }
        else
        {
            File.Copy(appExe, Path.Combine(staging, "OB2.exe"));
        }

        File.WriteAllText(Path.Combine(staging, "chrome-extension", "content.js"), "button with a close");
        File.WriteAllText(Path.Combine(staging, "portable.flag"), "portable");
        var zip = Path.Combine(Feed, UpdateChecker.ZipAsset);
        File.Delete(zip);
        ZipFile.CreateFromDirectory(staging, zip);

        var sums = Encoding.ASCII.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip)))}  OB2-portable.zip\r\n");
        File.WriteAllBytes(Path.Combine(Feed, UpdateChecker.ChecksumsAsset), sums);
        File.WriteAllText(Path.Combine(Feed, UpdateChecker.SignatureAsset), ReleaseSignature.Sign(sums, signedBy) + "\n");

        string Asset(string name) => $$"""{ "name": "{{name}}", "size": {{new FileInfo(Path.Combine(Feed, name)).Length}}, "browser_download_url": "{{_server.Url("/static/" + name)}}" }""";
        File.WriteAllText(Path.Combine(Feed, "latest.json"), $$"""
            {
              "tag_name": "v{{version}}", "name": "OB2 {{version}}", "body": "Notes", "draft": false, "prerelease": false,
              "html_url": "https://github.com/tahakadhim00-afk/OB2/releases/tag/v{{version}}",
              "assets": [ {{Asset(UpdateChecker.ZipAsset)}}, {{Asset(UpdateChecker.ChecksumsAsset)}}, {{Asset(UpdateChecker.SignatureAsset)}} ]
            }
            """);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
