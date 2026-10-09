using Microsoft.Data.Sqlite;
using OB2.Storage;
using OB2.Storage.Logging;
using OB2.Storage.Paths;
using OB2.Tests.Common;

namespace OB2.Integration.Tests;

/// <summary>The same startup sequence the WPF app runs, exercised without launching it.</summary>
public sealed class StorageBootstrapperTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _exeDir;
    private readonly string _roaming;

    public StorageBootstrapperTests()
    {
        _exeDir = Directory.CreateDirectory(_temp.Combine("OB2-portable")).FullName;
        _roaming = Directory.CreateDirectory(_temp.Combine("Roaming")).FullName;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private AppPathEnvironment Environment => new(_exeDir, _roaming);

    [Fact]
    public async Task Portable_first_run_creates_settings_database_and_log_beside_exe()
    {
        File.WriteAllText(Path.Combine(_exeDir, AppPaths.PortableFlagFileName), "");

        using (var context = await StorageBootstrapper.InitializeAsync(Environment))
        {
            context.Paths.IsPortable.Should().BeTrue();
            context.Migration.Applied.Should().Equal(1, 2);
        }

        File.Exists(Path.Combine(_exeDir, "settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(_exeDir, "data", "ob2.db")).Should().BeTrue();
        var log = Directory.GetFiles(Path.Combine(_exeDir, "data", "logs"), LogConfigurator.FilePrefix + "*.log").Should().ContainSingle().Subject;
        File.ReadAllText(log).Should().Contain("OB2 starting").And.Contain("portable").And.Contain("migrated from version 0 to 2");
        Directory.GetFileSystemEntries(_roaming).Should().BeEmpty("portable mode must not touch %APPDATA%");
    }

    [Fact]
    public async Task Per_user_mode_uses_appdata()
    {
        using (var context = await StorageBootstrapper.InitializeAsync(Environment))
        {
            context.Paths.IsPortable.Should().BeFalse();
        }

        File.Exists(Path.Combine(_roaming, "OB2", "settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(_roaming, "OB2", "data", "ob2.db")).Should().BeTrue();
        Directory.GetFileSystemEntries(_exeDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Second_start_is_idempotent_and_keeps_saved_settings()
    {
        File.WriteAllText(Path.Combine(_exeDir, AppPaths.PortableFlagFileName), "");

        using (var first = await StorageBootstrapper.InitializeAsync(Environment))
        {
            first.Settings.Downloads.Connections = 12;
            first.Settings.Folders.DefaultSaveFolder = @"E:\Incoming";
            await first.SettingsStore.SaveAsync(first.Settings);
        }

        using var second = await StorageBootstrapper.InitializeAsync(Environment);

        second.Migration.Applied.Should().BeEmpty();
        second.Migration.CurrentVersion.Should().Be(2);
        second.Settings.Downloads.Connections.Should().Be(12);
        second.Settings.Folders.DefaultSaveFolder.Should().Be(@"E:\Incoming");
    }

    [Fact]
    public async Task Only_the_first_process_on_a_data_folder_runs_recovery()
    {
        File.WriteAllText(Path.Combine(_exeDir, AppPaths.PortableFlagFileName), "");

        using (var first = await StorageBootstrapper.InitializeAsync(Environment))
        {
            first.InstanceLock.IsFirstInstance.Should().BeTrue();
            using var second = await StorageBootstrapper.InitializeAsync(Environment);
            second.InstanceLock.IsFirstInstance.Should().BeFalse("the window is already running on this folder");
        }

        using var later = await StorageBootstrapper.InitializeAsync(Environment);
        later.InstanceLock.IsFirstInstance.Should().BeTrue("the lock is released when the first process exits");
    }

    [Fact]
    public async Task Settings_problems_are_written_to_the_log()
    {
        File.WriteAllText(Path.Combine(_exeDir, AppPaths.PortableFlagFileName), "");
        File.WriteAllText(Path.Combine(_exeDir, "settings.json"), """{ "downloads": { "connections": 100 } }""");

        using (var context = await StorageBootstrapper.InitializeAsync(Environment))
        {
            context.Settings.Downloads.Connections.Should().Be(32);
        }

        var log = Directory.GetFiles(Path.Combine(_exeDir, "data", "logs")).Single();
        File.ReadAllText(log).Should().Contain("[WRN]").And.Contain("downloads.connections");
    }
}
