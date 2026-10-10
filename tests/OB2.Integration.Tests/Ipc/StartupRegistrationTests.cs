using Microsoft.Win32;
using OB2.Storage.Paths;
using OB2.Tests.Common;

namespace OB2.Integration.Tests.Ipc;

/// <summary>FR-31 against a scratch HKCU key, so the real Run key is never touched.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class StartupRegistrationTests : IDisposable
{
    private const string ScratchRoot = @"Software\OB2.Tests";
    private readonly string _keyPath = ScratchRoot + @"\run-" + Guid.NewGuid().ToString("N");
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
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
                }
            }
        }

        _temp.Dispose();
    }

    [Fact]
    public void Real_key_path_is_the_per_user_Run_key()
    {
        StartupRegistration.RunKeyPath.Should().Be(@"Software\Microsoft\Windows\CurrentVersion\Run");
    }

    [Fact]
    public void Enabling_writes_a_quoted_tray_start_command()
    {
        var startup = Create("C:\\Program Files (x86)\\OB2\\OB2.exe");

        startup.Sync(enabled: true, takeOver: false).Should().BeTrue();

        Registered().Should().Be("\"C:\\Program Files (x86)\\OB2\\OB2.exe\" --minimized");
        startup.IsEnabledHere.Should().BeTrue();
        startup.Sync(enabled: true, takeOver: false).Should().BeFalse("nothing to change");
    }

    [Fact]
    public void A_data_folder_given_on_the_command_line_is_kept()
    {
        var startup = new StartupRegistration("D:\\OB2\\OB2.exe", "D:\\Test Data\\", _keyPath);

        startup.Sync(enabled: true, takeOver: false);

        Registered().Should().Be("\"D:\\OB2\\OB2.exe\" --minimized --data \"D:\\Test Data\"");
    }

    [Fact]
    public void Disabling_removes_this_copys_value()
    {
        var startup = Create("D:\\OB2\\OB2.exe");
        startup.Sync(enabled: true, takeOver: false);

        startup.Sync(enabled: false, takeOver: false).Should().BeTrue();

        Registered().Should().BeNull();
    }

    [Fact]
    public void A_moved_folder_is_repaired_on_start()
    {
        // Registered from a folder that no longer exists.
        Create(_temp.Combine("old", "OB2.exe")).Sync(enabled: true, takeOver: false);
        var moved = CreateWithExe("new");

        moved.Sync(enabled: true, takeOver: false).Should().BeTrue();

        moved.IsEnabledHere.Should().BeTrue();
    }

    [Fact]
    public void Another_working_copy_keeps_its_value_on_start()
    {
        var other = CreateWithExe("usb");
        other.Sync(enabled: true, takeOver: false);
        var here = CreateWithExe("desktop");

        here.Sync(enabled: true, takeOver: false).Should().BeFalse();
        here.Sync(enabled: false, takeOver: false).Should().BeFalse("saving unrelated options must not remove another copy's value");

        other.IsEnabledHere.Should().BeTrue();
    }

    [Fact]
    public void Ticking_the_option_takes_the_value_over()
    {
        var other = CreateWithExe("usb");
        other.Sync(enabled: true, takeOver: false);
        var here = CreateWithExe("desktop");

        here.Sync(enabled: true, takeOver: true).Should().BeTrue();

        here.IsEnabledHere.Should().BeTrue();
        other.IsEnabledHere.Should().BeFalse();
    }

    [Fact]
    public void Unticking_the_option_removes_it_whichever_copy_wrote_it()
    {
        CreateWithExe("usb").Sync(enabled: true, takeOver: false);

        CreateWithExe("desktop").Sync(enabled: false, takeOver: true).Should().BeTrue();

        Registered().Should().BeNull();
    }

    [Fact]
    public void Disabled_and_absent_changes_nothing()
    {
        Create("D:\\OB2\\OB2.exe").Sync(enabled: false, takeOver: true).Should().BeFalse();
        Registry.CurrentUser.OpenSubKey(_keyPath).Should().BeNull("no key is created just to say no");
    }

    [Theory]
    [InlineData("\"C:\\A B\\OB2.exe\" --minimized", "C:\\A B\\OB2.exe")]
    [InlineData("C:\\Apps\\OB2.exe --minimized", "C:\\Apps\\OB2.exe")]
    [InlineData("C:\\Apps\\OB2.exe", "C:\\Apps\\OB2.exe")]
    [InlineData("  ", null)]
    [InlineData("\"unterminated", null)]
    public void ExecutableOf_reads_the_program_path(string command, string? expected)
    {
        StartupRegistration.ExecutableOf(command).Should().Be(expected);
    }

    private StartupRegistration Create(string exe) => new(exe, null, _keyPath);

    private StartupRegistration CreateWithExe(string folder)
    {
        var exe = _temp.Combine(folder, "OB2.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "stand-in");
        return Create(exe);
    }

    private string? Registered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(StartupRegistration.ValueName) as string;
    }
}
