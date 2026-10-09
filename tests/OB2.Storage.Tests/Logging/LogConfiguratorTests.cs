using OB2.Core.Settings;
using OB2.Storage.Logging;
using OB2.Tests.Common;

namespace OB2.Storage.Tests.Logging;

public sealed class LogConfiguratorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Creates_a_log_file()
    {
        var logs = _temp.Combine("logs");

        using (var logger = LogConfigurator.CreateLogger(logs, new LoggingSettings()))
        {
            logger.Information("hello {Name}", "OB2");
        }

        var file = Directory.GetFiles(logs, LogConfigurator.FilePrefix + "*.log").Should().ContainSingle().Subject;
        File.ReadAllText(file).Should().Contain("hello OB2").And.Contain("[INF]");
    }

    [Fact]
    public void Rolls_to_a_new_file_at_the_configured_size()
    {
        var logs = _temp.Combine("logs");
        var settings = new LoggingSettings { FileSizeLimitBytes = 4 * 1024 };
        var line = new string('x', 200);

        using (var logger = LogConfigurator.CreateLogger(logs, settings))
        {
            for (var i = 0; i < 100; i++)
            {
                logger.Information("event-{Index} {Line}", i.ToString("000", System.Globalization.CultureInfo.InvariantCulture), line);
            }
        }

        var files = Directory.GetFiles(logs, LogConfigurator.FilePrefix + "*.log");
        files.Length.Should().BeGreaterThan(1, "about 25 KB of events at a 4 KB limit must roll");
        files.Should().AllSatisfy(f => new FileInfo(f).Length.Should().BeLessThan(settings.FileSizeLimitBytes + 1024));

        // Nothing is dropped: every event is in exactly one file.
        var all = string.Concat(files.Select(File.ReadAllText));
        Enumerable.Range(0, 100).Should().AllSatisfy(i => all.Should().Contain($"event-{i:000} {line}"));
    }

    [Fact]
    public void Minimum_level_is_respected()
    {
        var logs = _temp.Combine("logs");

        using (var logger = LogConfigurator.CreateLogger(logs, new LoggingSettings { MinimumLevel = "Warning" }))
        {
            logger.Information("quiet");
            logger.Warning("loud");
        }

        var text = File.ReadAllText(Directory.GetFiles(logs).Single());
        text.Should().Contain("loud").And.NotContain("quiet");
    }
}
