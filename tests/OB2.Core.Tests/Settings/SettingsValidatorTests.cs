using OB2.Core.Settings;

namespace OB2.Core.Tests.Settings;

public sealed class SettingsValidatorTests
{
    [Fact]
    public void Defaults_need_no_changes()
    {
        var settings = new AppSettings();

        SettingsValidator.Normalize(settings).Should().BeEmpty();
    }

    [Fact]
    public void Defaults_match_the_prd()
    {
        var settings = new AppSettings();

        settings.Downloads.Connections.Should().Be(8);
        settings.Downloads.MaxRetries.Should().Be(5);
        settings.Downloads.ConnectTimeoutSeconds.Should().Be(30);
        settings.Downloads.ReadIdleTimeoutSeconds.Should().Be(60);
        settings.Downloads.AutoResumeOnStartup.Should().BeFalse();
        settings.Downloads.RememberAuth.Should().BeFalse();
        settings.Capture.MinSizeBytes.Should().Be(1024 * 1024);
        settings.Capture.ConfirmBeforeAdding.Should().BeTrue();
        settings.General.MinimizeToTrayOnClose.Should().BeTrue();
        settings.General.StartWithWindows.Should().BeFalse();
        settings.Logging.RetainedDays.Should().Be(7);
        settings.Logging.FileSizeLimitBytes.Should().Be(10 * 1024 * 1024);
        settings.History.RetentionDays.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(32, 32)]
    [InlineData(33, 32)]
    public void Connections_are_clamped_to_1_through_32(int configured, int expected)
    {
        var settings = new AppSettings();
        settings.Downloads.Connections = configured;

        var changes = SettingsValidator.Normalize(settings);

        settings.Downloads.Connections.Should().Be(expected);
        changes.Should().HaveCount(configured == expected ? 0 : 1);
    }

    [Fact]
    public void Null_sections_from_json_are_restored()
    {
        var settings = new AppSettings { Downloads = null!, Logging = null!, Capture = null! };

        SettingsValidator.Normalize(settings);

        settings.Downloads.Connections.Should().Be(8);
        settings.Logging.RetainedDays.Should().Be(7);
        settings.Capture.Extensions.Should().NotBeEmpty();
    }

    [Fact]
    public void Invalid_values_are_reset_and_reported()
    {
        var settings = new AppSettings();
        settings.Downloads.UserAgent = " ";
        settings.Logging.MinimumLevel = "Loud";
        settings.Logging.FileSizeLimitBytes = 10;
        settings.SpeedLimits.GlobalBytesPerSecond = 0;
        settings.History.RetentionDays = -1;

        var changes = SettingsValidator.Normalize(settings);

        changes.Should().HaveCount(5);
        settings.Downloads.UserAgent.Should().Be(DownloadSettings.DefaultUserAgent);
        settings.Logging.MinimumLevel.Should().Be("Information");
        settings.Logging.FileSizeLimitBytes.Should().Be(LoggingSettings.MinFileSizeLimitBytes);
        settings.SpeedLimits.GlobalBytesPerSecond.Should().BeNull();
        settings.History.RetentionDays.Should().BeNull();
    }
}
