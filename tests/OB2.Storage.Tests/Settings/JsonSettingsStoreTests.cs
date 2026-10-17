using OB2.Core.Settings;
using OB2.Storage.Settings;
using OB2.Tests.Common;

namespace OB2.Storage.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Missing_file_yields_defaults_and_writes_them()
    {
        var store = new JsonSettingsStore(_temp.Combine("settings.json"));

        var result = await store.LoadAsync();

        result.Issues.Should().BeEmpty();
        result.Settings.Should().BeEquivalentTo(new AppSettings());
        File.Exists(store.FilePath).Should().BeTrue();
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        var store = new JsonSettingsStore(_temp.Combine("settings.json"));
        var settings = new AppSettings();
        settings.Downloads.Connections = 16;
        settings.Downloads.MaxRetries = 7;
        settings.Downloads.UserAgent = "OB2-test";
        settings.Downloads.AutoResumeOnStartup = true;
        settings.Folders.DefaultSaveFolder = @"D:\Downloads\مجلد";
        settings.SpeedLimits.GlobalBytesPerSecond = 512_000;
        settings.Capture.Extensions = ["zip", "iso"];
        settings.Capture.ExcludedDomains = ["example.com"];
        settings.Capture.ConfirmBeforeAdding = false;
        settings.General.MinimizeToTrayOnClose = false;
        settings.Logging.MinimumLevel = "Debug";
        settings.History.RetentionDays = 90;

        await store.SaveAsync(settings);
        var loaded = await new JsonSettingsStore(store.FilePath).LoadAsync();

        loaded.Issues.Should().BeEmpty();
        loaded.Settings.Should().BeEquivalentTo(settings);
    }

    [Fact]
    public async Task File_uses_camel_case_and_is_human_editable()
    {
        var store = new JsonSettingsStore(_temp.Combine("settings.json"));
        await store.SaveAsync(new AppSettings());

        var json = await File.ReadAllTextAsync(store.FilePath);

        json.Should().Contain("\"downloads\": {").And.Contain("\"connections\": 8");
    }

    [Fact]
    public async Task Hand_edited_file_with_comments_and_missing_keys_loads()
    {
        var path = _temp.Combine("settings.json");
        await File.WriteAllTextAsync(path, """
            {
              // user note
              "version": 2,
              "downloads": { "connections": 4, },
            }
            """);

        var result = await new JsonSettingsStore(path).LoadAsync();

        result.Issues.Should().BeEmpty();
        result.Settings.Downloads.Connections.Should().Be(4);
        result.Settings.Downloads.MaxRetries.Should().Be(5);
        result.Settings.Logging.RetainedDays.Should().Be(7);
    }

    [Fact]
    public async Task Out_of_range_values_are_clamped_and_reported()
    {
        var path = _temp.Combine("settings.json");
        await File.WriteAllTextAsync(path, """{ "version": 2, "downloads": { "connections": 64 } }""");

        var result = await new JsonSettingsStore(path).LoadAsync();

        result.Settings.Downloads.Connections.Should().Be(32);
        result.Issues.Should().ContainSingle().Which.Should().Contain("downloads.connections");
    }

    [Fact]
    public async Task Corrupt_file_is_backed_up_and_replaced_with_defaults()
    {
        var path = _temp.Combine("settings.json");
        await File.WriteAllTextAsync(path, "{ this is not json");

        var result = await new JsonSettingsStore(path).LoadAsync();

        result.Settings.Should().BeEquivalentTo(new AppSettings());
        result.Issues.Should().ContainSingle().Which.Should().Contain("could not be parsed");
        var backup = Directory.GetFiles(_temp.Path, "settings.json.corrupt-*").Should().ContainSingle().Subject;
        (await File.ReadAllTextAsync(backup)).Should().Be("{ this is not json");
        (await new JsonSettingsStore(path).LoadAsync()).Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task The_theme_round_trips_by_name()
    {
        var path = _temp.Combine("settings.json");
        var store = new JsonSettingsStore(path);
        var settings = new AppSettings();
        settings.General.Theme = AppTheme.Dark;

        await store.SaveAsync(settings);

        // Written as a name, so the file stays readable and hand-editable (§10.2).
        (await File.ReadAllTextAsync(path)).Should().Contain("\"theme\": \"Dark\"");
        (await new JsonSettingsStore(path).LoadAsync()).Settings.General.Theme.Should().Be(AppTheme.Dark);
    }

    [Fact]
    public async Task A_version_1_file_is_upgraded_once_and_written_back()
    {
        var path = _temp.Combine("settings.json");
        await File.WriteAllTextAsync(path, """{ "version": 1, "downloads": { "connections": 4 } }""");

        var result = await new JsonSettingsStore(path).LoadAsync();

        // FR-31: the user never had "start with Windows" on, so the new default must not claim they did.
        result.Settings.General.StartWithWindows.Should().BeFalse();
        result.Settings.Version.Should().Be(AppSettings.CurrentVersion);
        result.Issues.Should().ContainSingle().Which.Should().Contain("startWithWindows");

        // Written back, so the next launch sees a current file and reports nothing.
        var reloaded = await new JsonSettingsStore(path).LoadAsync();
        reloaded.Issues.Should().BeEmpty();
        reloaded.Settings.General.StartWithWindows.Should().BeFalse();
        reloaded.Settings.Downloads.Connections.Should().Be(4);
    }

    [Fact]
    public async Task Newer_version_is_reported_not_rejected()
    {
        var path = _temp.Combine("settings.json");
        await File.WriteAllTextAsync(path, """{ "version": 99, "futureSetting": true }""");

        var result = await new JsonSettingsStore(path).LoadAsync();

        result.Issues.Should().ContainSingle().Which.Should().Contain("newer");
        result.Settings.Downloads.Connections.Should().Be(8);
    }
}
