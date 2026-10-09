using OB2.Core.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace OB2.Storage.Logging;

/// <summary>
/// Rolling local log files (NFR Logging): one file per day, a new file when the size limit
/// is hit, files older than the retention window deleted. Nothing is sent over the network.
/// </summary>
public static class LogConfigurator
{
    public const string FilePrefix = "ob2-";

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static Logger CreateLogger(string logsDirectory, LoggingSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(logsDirectory);

        var level = Enum.TryParse<LogEventLevel>(settings.MinimumLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Information;

        return new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logsDirectory, FilePrefix + ".log"),
                outputTemplate: OutputTemplate,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                fileSizeLimitBytes: settings.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(settings.RetainedDays))
            .CreateLogger();
    }
}
