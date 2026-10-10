using OB2.Storage.Paths;
using OB2.Tests.Common;

namespace OB2.Storage.Tests.Paths;

public sealed class AppPathsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Portable_flag_puts_everything_beside_the_executable()
    {
        var exeDir = Directory.CreateDirectory(_temp.Combine("app")).FullName;
        File.WriteAllText(Path.Combine(exeDir, AppPaths.PortableFlagFileName), "");

        var paths = AppPaths.Resolve(new AppPathEnvironment(exeDir + Path.DirectorySeparatorChar, _temp.Combine("roaming")));

        paths.IsPortable.Should().BeTrue();
        paths.Root.Should().Be(exeDir);
        paths.SettingsFile.Should().Be(Path.Combine(exeDir, "settings.json"));
        paths.DatabaseFile.Should().Be(Path.Combine(exeDir, "data", "ob2.db"));
        paths.LogsDirectory.Should().Be(Path.Combine(exeDir, "data", "logs"));
    }

    [Fact]
    public void Without_flag_uses_appdata_ob2()
    {
        var exeDir = Directory.CreateDirectory(_temp.Combine("app")).FullName;
        var roaming = _temp.Combine("roaming");

        var paths = AppPaths.Resolve(new AppPathEnvironment(exeDir, roaming));

        paths.IsPortable.Should().BeFalse();
        paths.Root.Should().Be(Path.Combine(roaming, "OB2"));
        paths.SettingsFile.Should().Be(Path.Combine(roaming, "OB2", "settings.json"));
        paths.DatabaseFile.Should().Be(Path.Combine(roaming, "OB2", "data", "ob2.db"));
    }

    [Fact]
    public void Read_only_portable_folder_falls_back_to_appdata()
    {
        // §12.3: e.g. the zip extracted to a read-only share or a write-protected USB stick.
        var exeDir = Directory.CreateDirectory(_temp.Combine("app")).FullName;
        File.WriteAllText(Path.Combine(exeDir, AppPaths.PortableFlagFileName), "");
        var roaming = _temp.Combine("roaming");

        var paths = AppPaths.Resolve(new AppPathEnvironment(exeDir, roaming) { IsWritable = _ => false });

        paths.IsPortable.Should().BeFalse();
        paths.FellBackFromReadOnly.Should().BeTrue();
        paths.Root.Should().Be(Path.Combine(roaming, "OB2"));
    }

    [Fact]
    public void Default_writability_probe_leaves_no_files_behind()
    {
        var exeDir = Directory.CreateDirectory(_temp.Combine("app")).FullName;
        File.WriteAllText(Path.Combine(exeDir, AppPaths.PortableFlagFileName), "");

        AppPaths.Resolve(new AppPathEnvironment(exeDir, _temp.Combine("roaming"))).IsPortable.Should().BeTrue();
        Directory.GetFiles(exeDir).Should().ContainSingle("only portable.flag remains");
    }

    [Fact]
    public void A_directory_named_like_the_flag_does_not_count()
    {
        var exeDir = Directory.CreateDirectory(_temp.Combine("app")).FullName;
        Directory.CreateDirectory(Path.Combine(exeDir, AppPaths.PortableFlagFileName));

        AppPaths.Resolve(new AppPathEnvironment(exeDir, _temp.Combine("roaming"))).IsPortable.Should().BeFalse();
    }

    [Fact]
    public void EnsureCreated_creates_data_and_logs_folders()
    {
        var paths = new AppPaths(_temp.Combine("root"), IsPortable: true);

        paths.EnsureCreated();

        Directory.Exists(paths.DataDirectory).Should().BeTrue();
        Directory.Exists(paths.LogsDirectory).Should().BeTrue();
    }
}
