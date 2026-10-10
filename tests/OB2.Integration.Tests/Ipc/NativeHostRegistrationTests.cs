using System.Text.Json;
using Microsoft.Win32;
using OB2.Shared;
using OB2.Shared.Ipc;
using OB2.Tests.Common;

namespace OB2.Integration.Tests.Ipc;

/// <summary>
/// AC-9 and §12.6, against scratch HKCU keys (one per browser) so the real Chrome and Edge
/// registrations are never touched.
/// HKCU writes need no elevation, so passing here means no admin prompt.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NativeHostRegistrationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private const string ScratchRoot = @"Software\OB2.Tests";
    private readonly string _keyRoot = ScratchRoot + @"\" + Guid.NewGuid().ToString("N");

    private string ChromeKey => _keyRoot + @"\chrome";

    private string EdgeKey => _keyRoot + @"\edge";

    private string[] Keys => [ChromeKey, EdgeKey];

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keyRoot, throwOnMissingSubKey: false);
        using (var root = Registry.CurrentUser.OpenSubKey(ScratchRoot))
        {
            if (root is { SubKeyCount: 0, ValueCount: 0 })
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKey(ScratchRoot, throwOnMissingSubKey: false);
                }
                catch (InvalidOperationException)
                {
                    // A test running in parallel just created its key.
                }
            }
        }

        _temp.Dispose();
    }

    [Fact]
    public void Chrome_key_path_is_the_one_from_the_PRD()
    {
        NativeHostRegistration.ChromeKeyPath.Should().Be(@"Software\Google\Chrome\NativeMessagingHosts\com.ob2.host");
    }

    [Fact]
    public void Chrome_and_Edge_are_both_registered_by_default()
    {
        NativeHostRegistration.BrowserKeyPaths.Should().Equal(
            @"Software\Google\Chrome\NativeMessagingHosts\com.ob2.host",
            @"Software\Microsoft\Edge\NativeMessagingHosts\com.ob2.host");
    }

    [Fact]
    public void Register_writes_the_HKCU_values_and_a_manifest_for_the_pinned_extension()
    {
        var registration = Create(_temp.Path);

        registration.Register();

        foreach (var keyPath in Keys)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            key!.GetValue(null).Should().Be(registration.ManifestPath, keyPath);
        }

        registration.IsRegistered.Should().BeTrue();
        using var manifest = JsonDocument.Parse(File.ReadAllText(registration.ManifestPath));
        var root = manifest.RootElement;
        root.GetProperty("name").GetString().Should().Be(ProtocolInfo.HostName);
        root.GetProperty("type").GetString().Should().Be("stdio");
        root.GetProperty("path").GetString().Should().Be(Path.Combine(_temp.Path, "host.exe"));
        root.GetProperty("allowed_origins").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("chrome-extension://mgmmgplfngnhcencbhpfagnjcnenflpm/");
    }

    [Fact]
    public void Unregister_removes_both_keys_and_the_manifest()
    {
        var registration = Create(_temp.Path);
        registration.Register();

        registration.Unregister();

        Registry.CurrentUser.OpenSubKey(ChromeKey).Should().BeNull();
        Registry.CurrentUser.OpenSubKey(EdgeKey).Should().BeNull();
        File.Exists(registration.ManifestPath).Should().BeFalse();
        registration.IsRegistered.Should().BeFalse();
    }

    [Fact]
    public void Unregister_when_not_registered_is_harmless()
    {
        var act = () => Create(_temp.Path).Unregister();
        act.Should().NotThrow();
    }

    [Fact]
    public void Manifest_can_live_outside_a_read_only_host_folder()
    {
        var hostFolder = _temp.Combine("app");
        var manifestFolder = _temp.Combine("appdata");
        var registration = new NativeHostRegistration(hostFolder, manifestFolder, Keys);

        registration.Register();

        registration.ManifestPath.Should().StartWith(manifestFolder);
        JsonDocument.Parse(File.ReadAllText(registration.ManifestPath)).RootElement.GetProperty("path").GetString()
            .Should().Be(Path.Combine(hostFolder, "host.exe"));
    }

    [Fact]
    public void A_fresh_registration_needs_no_repair()
    {
        var registration = CreateWithHost(_temp.Path);
        registration.NeedsRepair().Should().BeFalse("nothing is registered");

        registration.Register();

        registration.NeedsRepair().Should().BeFalse();
        registration.IsRegisteredHere.Should().BeTrue();
    }

    [Fact]
    public void Moving_the_portable_folder_is_detected_and_repaired()
    {
        // §12.6: registered from one folder, then the whole folder is moved.
        var oldFolder = _temp.Combine("old");
        CreateWithHost(oldFolder).Register();
        Directory.Move(oldFolder, _temp.Combine("new"));

        var moved = Create(_temp.Combine("new"));
        moved.NeedsRepair().Should().BeTrue();

        moved.Register();

        moved.NeedsRepair().Should().BeFalse();
        moved.RegisteredManifestPath().Should().Be(moved.ManifestPath);
    }

    [Fact]
    public void A_manifest_pointing_at_a_missing_host_needs_repair()
    {
        var registration = CreateWithHost(_temp.Path);
        registration.Register();
        File.Delete(registration.HostPath);

        registration.NeedsRepair().Should().BeTrue();
    }

    [Fact]
    public void Another_working_copy_keeps_its_registration()
    {
        // Decision D-55: two copies on one PC must not take the registration from each other on start.
        var other = CreateWithHost(_temp.Combine("usb"));
        other.Register();
        var here = CreateWithHost(_temp.Combine("desktop"));

        here.NeedsRepair().Should().BeFalse();
        here.IsRegistered.Should().BeTrue();
        here.IsRegisteredHere.Should().BeFalse();

        here.Register(); // the explicit button moves it

        here.IsRegisteredHere.Should().BeTrue();
        other.IsRegisteredHere.Should().BeFalse();
    }

    [Fact]
    public void A_corrupt_manifest_needs_repair()
    {
        var registration = Create(_temp.Path);
        registration.Register();
        File.WriteAllText(registration.ManifestPath, "{ not json");

        registration.NeedsRepair().Should().BeTrue();
    }

    [Fact]
    public void A_registration_from_before_Edge_support_is_completed_on_start()
    {
        var registration = CreateWithHost(_temp.Path);
        new NativeHostRegistration(_temp.Path, _temp.Path, [ChromeKey]).Register(); // Chrome only, as in 0.6

        registration.IsRegisteredHere.Should().BeFalse();
        registration.NeedsRepair().Should().BeTrue();

        registration.Register();

        registration.IsRegisteredHere.Should().BeTrue();
        registration.NeedsRepair().Should().BeFalse();
    }

    [Fact]
    public void A_browser_registered_to_another_working_copy_is_not_taken_on_start()
    {
        var usb = _temp.Combine("usb");
        CreateWithHost(usb);
        new NativeHostRegistration(usb, usb, [ChromeKey]).Register();
        var desktop = _temp.Combine("desktop");
        var here = CreateWithHost(desktop);
        new NativeHostRegistration(desktop, desktop, [EdgeKey]).Register();

        here.NeedsRepair().Should().BeFalse("Chrome belongs to the other copy (D-55)");
    }

    private NativeHostRegistration Create(string folder) => new(folder, folder, Keys);

    private NativeHostRegistration CreateWithHost(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, NativeHostRegistration.HostExeName), "stand-in");
        return Create(folder);
    }
}

/// <summary>How host.exe finds the pipe name (decision D-52).</summary>
public sealed class HostLocatorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string AppFolder => _temp.Combine("app");

    private string AppData => _temp.Combine("roaming");

    public HostLocatorTests()
    {
        Directory.CreateDirectory(AppFolder);
        Directory.CreateDirectory(Path.Combine(AppData, "OB2"));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Portable_settings_win_when_the_flag_exists()
    {
        File.WriteAllText(Path.Combine(AppFolder, "portable.flag"), string.Empty);
        File.WriteAllText(Path.Combine(AppFolder, "settings.json"), """{ "ipc": { "pipeName": "OB2-portable" } }""");
        File.WriteAllText(Path.Combine(AppData, "OB2", "settings.json"), """{ "ipc": { "pipeName": "OB2-roaming" } }""");

        HostLocator.ReadPipeName(AppFolder, AppData).Should().Be("OB2-portable");
    }

    [Fact]
    public void Without_the_flag_settings_come_from_APPDATA()
    {
        File.WriteAllText(Path.Combine(AppFolder, "settings.json"), """{ "ipc": { "pipeName": "OB2-portable" } }""");
        File.WriteAllText(Path.Combine(AppData, "OB2", "settings.json"), """{ "ipc": { "pipeName": "OB2-roaming" } }""");

        HostLocator.ReadPipeName(AppFolder, AppData).Should().Be("OB2-roaming");
    }

    [Fact]
    public void A_read_only_portable_copy_falls_back_to_APPDATA()
    {
        // The flag exists, but the app could not write settings beside it (decision D-45).
        File.WriteAllText(Path.Combine(AppFolder, "portable.flag"), string.Empty);
        File.WriteAllText(Path.Combine(AppData, "OB2", "settings.json"), """{ "ipc": { "pipeName": "OB2-roaming" } }""");

        HostLocator.ReadPipeName(AppFolder, AppData).Should().Be("OB2-roaming");
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted_like_the_app_does()
    {
        File.WriteAllText(Path.Combine(AppData, "OB2", "settings.json"), """
            {
              // written by hand
              "ipc": { "pipeName": "OB2-abc", },
            }
            """);

        HostLocator.ReadPipeName(AppFolder, AppData).Should().Be("OB2-abc");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "ipc": { } }""")]
    [InlineData("""{ "ipc": { "pipeName": null } }""")]
    [InlineData("""{ "ipc": { "pipeName": "" } }""")]
    public void Missing_or_broken_settings_mean_no_pipe(string json)
    {
        File.WriteAllText(Path.Combine(AppData, "OB2", "settings.json"), json);

        HostLocator.ReadPipeName(AppFolder, AppData).Should().BeNull();
    }

    [Fact]
    public void A_settings_file_held_open_by_the_app_can_still_be_read()
    {
        var file = Path.Combine(AppData, "OB2", "settings.json");
        File.WriteAllText(file, """{ "ipc": { "pipeName": "OB2-open" } }""");
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        HostLocator.ReadPipeName(AppFolder, AppData).Should().Be("OB2-open");
    }
}
