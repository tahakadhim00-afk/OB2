using System.Security.Cryptography;
using System.Text;
using OB2.Core.Updates;
using OB2.Tests.Common;

namespace OB2.Core.Tests.Updates;

/// <summary>D-84, D-85: release versions, signatures, GitHub's release JSON, and swapping the program files.</summary>
public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("1.2.0", 1, 2, 0)]
    [InlineData("V10.0.3", 10, 0, 3)]
    [InlineData(" v1.2 ", 1, 2, 0)]
    public void Reads_release_tags(string tag, int major, int minor, int patch)
    {
        ReleaseVersion.TryParse(tag, out var version).Should().BeTrue();
        version.Should().Be(new Version(major, minor, patch));
    }

    [Theory]
    [InlineData("v1.2.0-beta")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_anything_that_is_not_a_release_version(string? tag) =>
        ReleaseVersion.TryParse(tag, out _).Should().BeFalse();

    [Fact]
    public void A_two_part_version_equals_its_three_part_form_so_it_is_not_offered_as_newer()
    {
        ReleaseVersion.TryParse("1.2", out var two).Should().BeTrue();
        ReleaseVersion.TryParse("1.2.0", out var three).Should().BeTrue();

        two.Should().Be(three);
        (two > three).Should().BeFalse();
    }

    [Fact]
    public void The_apps_version_drops_the_commit()
    {
        ReleaseVersion.OfApp("1.1.0+5783804c35b44a1e76fb2555feb5959ef3f70982").Should().Be(new Version(1, 1, 0));
        ReleaseVersion.Text(new Version(1, 1)).Should().Be("1.1.0");
    }
}

public sealed class ReleaseSignatureTests
{
    private static readonly byte[] Sums = Encoding.ASCII.GetBytes("abc  OB2-portable.zip\r\n");

    [Fact]
    public void A_signature_by_a_trusted_key_is_valid()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = ReleaseSignature.Sign(Sums, key);

        ReleaseSignature.Verify(Sums, signature, [ReleaseSignature.PublicKeyOf(key)]).Should().BeTrue();
        ReleaseSignature.Verify(Sums, signature + "\n", [ReleaseSignature.PublicKeyOf(key)]).Should().BeTrue("the .sig file ends with a newline");
    }

    [Fact]
    public void A_changed_checksum_file_is_not_valid()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = ReleaseSignature.Sign(Sums, key);
        var changed = (byte[])Sums.Clone();
        changed[0] = (byte)'b';

        ReleaseSignature.Verify(changed, signature, [ReleaseSignature.PublicKeyOf(key)]).Should().BeFalse();
    }

    [Fact]
    public void A_signature_by_any_other_key_is_not_valid()
    {
        using var developer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        ReleaseSignature.Verify(Sums, ReleaseSignature.Sign(Sums, attacker), [ReleaseSignature.PublicKeyOf(developer)]).Should().BeFalse();
        ReleaseSignature.Verify(Sums, ReleaseSignature.Sign(Sums, attacker), []).Should().BeFalse("no trusted key, nothing is valid");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all")]
    [InlineData("AAAA")]
    public void Malformed_signatures_are_not_valid(string? signature)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        ReleaseSignature.Verify(Sums, signature, [ReleaseSignature.PublicKeyOf(key)]).Should().BeFalse();
    }

    [Fact]
    public void Any_of_several_trusted_keys_will_do_and_a_broken_entry_is_skipped()
    {
        using var old = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        ReleaseSignature.Verify(Sums, ReleaseSignature.Sign(Sums, current), ["garbage", ReleaseSignature.PublicKeyOf(old), ReleaseSignature.PublicKeyOf(current)])
            .Should().BeTrue();
    }

    [Fact]
    public void The_shipped_key_list_holds_only_P256_public_keys()
    {
        foreach (var key in ReleaseKey.Trusted)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            ecdsa.KeySize.Should().Be(256);
        }
    }
}

public sealed class UpdateCheckerParseTests
{
    private static readonly Version Current = new(1, 1, 0);

    private static string Release(string tag, bool prerelease = false, bool draft = false, bool withSignature = true) => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "OB2 {{tag.TrimStart('v')}}",
          "body": "## Highlights\n- Updates from inside the app",
          "html_url": "https://github.com/tahakadhim00-afk/OB2/releases/tag/{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "OB2-portable.zip", "size": 69285492, "browser_download_url": "https://github.com/tahakadhim00-afk/OB2/releases/download/{{tag}}/OB2-portable.zip" },
            { "name": "SHA256SUMS.txt", "size": 235, "browser_download_url": "https://github.com/tahakadhim00-afk/OB2/releases/download/{{tag}}/SHA256SUMS.txt" }
            {{(withSignature ? $", {{ \"name\": \"SHA256SUMS.txt.sig\", \"size\": 89, \"browser_download_url\": \"https://github.com/tahakadhim00-afk/OB2/releases/download/{tag}/SHA256SUMS.txt.sig\" }}" : string.Empty)}}
          ]
        }
        """;

    [Fact]
    public void A_newer_release_with_its_three_files_can_be_installed()
    {
        var release = UpdateChecker.Parse(Encoding.UTF8.GetBytes(Release("v1.2.0")), Current)!;

        release.Version.Should().Be(new Version(1, 2, 0));
        release.Title.Should().Be("OB2 1.2.0");
        release.Notes.Should().Contain("Updates from inside the app");
        release.Page.AbsoluteUri.Should().EndWith("/releases/tag/v1.2.0");
        release.Zip!.AbsoluteUri.Should().EndWith("/v1.2.0/OB2-portable.zip");
        release.ZipSize.Should().Be(69285492);
        release.CanInstall.Should().BeTrue();
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("v1.0.9")]
    [InlineData("v1.1")]
    public void The_same_or_an_older_version_is_no_update(string tag) =>
        UpdateChecker.Parse(Encoding.UTF8.GetBytes(Release(tag)), Current).Should().BeNull();

    [Fact]
    public void Pre_releases_and_drafts_are_never_offered()
    {
        UpdateChecker.Parse(Encoding.UTF8.GetBytes(Release("v2.0.0", prerelease: true)), Current).Should().BeNull();
        UpdateChecker.Parse(Encoding.UTF8.GetBytes(Release("v2.0.0", draft: true)), Current).Should().BeNull();
    }

    [Fact]
    public void A_release_without_its_signature_is_shown_but_only_its_page_is_offered()
    {
        var release = UpdateChecker.Parse(Encoding.UTF8.GetBytes(Release("v1.2.0", withSignature: false)), Current)!;

        release.CanInstall.Should().BeFalse();
        release.Page.Should().NotBeNull();
    }

    [Fact]
    public void A_tag_that_is_not_a_version_is_no_update() =>
        UpdateChecker.Parse(Encoding.UTF8.GetBytes(Release("nightly")), Current).Should().BeNull();

    [Fact]
    public void An_answer_that_is_not_JSON_is_a_readable_error()
    {
        var act = () => UpdateChecker.Parse(Encoding.UTF8.GetBytes("<html>rate limited</html>"), Current);

        act.Should().Throw<UpdateException>().WithMessage("*could not be read*");
    }
}

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _install;
    private readonly string _update;

    public UpdateInstallerTests()
    {
        _install = _temp.Combine("OB2");
        _update = _temp.Combine("update");

        // The copy in use: a portable folder with its own settings, data and tools.
        Write(_install, "OB2.exe", "old app");
        Write(_install, "host.exe", "old host");
        Write(_install, "README.txt", "old readme");
        Write(_install, "portable.flag", "this copy is portable");
        Write(_install, "settings.json", "{ \"mine\": true }");
        Write(_install, "host-manifest.json", "{ \"path\": \"here\" }");
        Write(_install, Path.Combine("data", "ob2.db"), "my downloads");
        Write(_install, Path.Combine("tools", "yt-dlp.exe"), "tool");
        Write(_install, Path.Combine("chrome-extension", "manifest.json"), "manifest");
        Write(_install, Path.Combine("chrome-extension", "content.js"), "old content");
        Write(_install, Path.Combine("chrome-extension", "gone.js"), "no longer shipped");

        // The unpacked release.
        Write(_update, "OB2.exe", "new app");
        Write(_update, "host.exe", "new host");
        Write(_update, "README.txt", "new readme");
        Write(_update, "LICENSE.txt", "MIT");
        Write(_update, "portable.flag", "release default");
        Write(_update, Path.Combine("chrome-extension", "manifest.json"), "manifest");
        Write(_update, Path.Combine("chrome-extension", "content.js"), "new content");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Replaces_the_program_files_and_keeps_the_previous_ones_aside()
    {
        var record = UpdateInstaller.Install(_update, _install, new Version(1, 1, 0), new Version(1, 2, 0));

        Read("OB2.exe").Should().Be("new app");
        Read("OB2.exe.old").Should().Be("old app");
        Read("host.exe").Should().Be("new host");
        Read("LICENSE.txt").Should().Be("MIT", "a file new in this release is added");
        Read(Path.Combine("chrome-extension", "content.js")).Should().Be("new content");
        File.Exists(Path.Combine(_install, "chrome-extension", "gone.js")).Should().BeFalse("a file the release no longer ships is moved aside");
        record.From.Should().Be("1.1.0");
        record.To.Should().Be("1.2.0");
        record.ExtensionChanged.Should().BeTrue();
    }

    [Fact]
    public void Never_touches_the_copys_mode_settings_data_or_tools()
    {
        UpdateInstaller.Install(_update, _install, new Version(1, 1, 0), new Version(1, 2, 0));

        Read("portable.flag").Should().Be("this copy is portable");
        Read("settings.json").Should().Be("{ \"mine\": true }");
        Read("host-manifest.json").Should().Be("{ \"path\": \"here\" }");
        Read(Path.Combine("data", "ob2.db")).Should().Be("my downloads");
        Read(Path.Combine("tools", "yt-dlp.exe")).Should().Be("tool");
    }

    [Fact]
    public void An_unchanged_extension_needs_no_reload_and_a_copy_without_one_is_not_told_to()
    {
        Write(_update, Path.Combine("chrome-extension", "content.js"), "old content");
        Write(_update, Path.Combine("chrome-extension", "gone.js"), "no longer shipped");
        UpdateInstaller.Install(_update, _install, new Version(1, 1, 0), new Version(1, 2, 0)).ExtensionChanged.Should().BeFalse();

        var bare = _temp.Combine("bare");
        Write(bare, "OB2.exe", "old app");
        UpdateInstaller.Install(_update, bare, new Version(1, 1, 0), new Version(1, 2, 0)).ExtensionChanged.Should().BeFalse();
    }

    [Fact]
    public void A_file_that_cannot_be_replaced_puts_everything_back()
    {
        using (File.Open(Path.Combine(_install, "README.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => UpdateInstaller.Install(_update, _install, new Version(1, 1, 0), new Version(1, 2, 0));

            act.Should().Throw<UpdateException>().WithMessage("*nothing was changed*");
        }

        Read("OB2.exe").Should().Be("old app");
        Read("host.exe").Should().Be("old host");
        Read(Path.Combine("chrome-extension", "content.js")).Should().Be("old content");
        File.Exists(Path.Combine(_install, "LICENSE.txt")).Should().BeFalse();
        Directory.EnumerateFiles(_install, "*.old", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void Going_back_restores_the_previous_version_exactly()
    {
        var before = Snapshot();
        var record = UpdateInstaller.Install(_update, _install, new Version(1, 1, 0), new Version(1, 2, 0));

        UpdateInstaller.Undo(_install, record);

        Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public void After_the_new_version_starts_the_previous_files_are_removed()
    {
        var record = UpdateInstaller.Install(_update, _install, new Version(1, 1, 0), new Version(1, 2, 0));

        UpdateInstaller.RemoveLeftovers(_install, record).Should().BeEmpty();

        Directory.EnumerateFiles(_install, "*.old", SearchOption.AllDirectories).Should().BeEmpty();
        Read("OB2.exe").Should().Be("new app");
    }

    [Fact]
    public void A_file_opened_the_way_a_running_program_is_can_still_be_replaced()
    {
        // Windows maps a running exe with delete sharing, which allows a rename but not an overwrite.
        using var running = File.Open(Path.Combine(_install, "OB2.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        UpdateInstaller.Install(_update, _install, new Version(1, 1, 0), new Version(1, 2, 0));

        Read("OB2.exe").Should().Be("new app");
    }

    [Fact]
    public async Task The_install_record_survives_a_restart()
    {
        var record = UpdateInstaller.Install(_update, _install, new Version(1, 1, 0), new Version(1, 2, 0));
        record.Resume = [4, 7];
        var path = _temp.Combine("updates", "installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await record.SaveAsync(path);
        var loaded = InstallRecord.Load(path)!;

        loaded.Should().BeEquivalentTo(record);
        InstallRecord.Load(_temp.Combine("nothing.json")).Should().BeNull();
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_install, relative));

    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_install, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(_install, f), File.ReadAllText);
}
