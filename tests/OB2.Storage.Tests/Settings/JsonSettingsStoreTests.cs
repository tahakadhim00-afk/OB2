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
        await File.WriteAllTextAsync(path, """{ "downloads": { "connections": 64 } }""");

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
    public async Task Newer_version_is_reported_not_rejected()
    {
        var path = _temp.Combine("settings.json");
        await File.WriteAllTextAsync(path, """{ "version": 99, "futureSetting": true }""");

        var result = await new JsonSettingsStore(path).LoadAsync();

        result.Issues.Should().ContainSingle().Which.Should().Contain("newer");
        result.Settings.Downloads.Connections.Should().Be(8);
    }
}
