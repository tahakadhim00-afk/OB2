using System.Globalization;

namespace OB2.Core.Settings;

/// <summary>
/// Clamps out-of-range values loaded from a hand-edited or older settings file so the
/// rest of the app can trust them, and carries a file written by an older version forward.
/// Returns a description of every change made.
/// </summary>
public static class SettingsValidator
{
    private static readonly string[] LogLevels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    public static IReadOnlyList<string> Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var changes = new List<string>();

        settings.Downloads ??= new DownloadSettings();
        settings.Folders ??= new FolderSettings();
        settings.SpeedLimits ??= new SpeedLimitSettings();
        settings.Capture ??= new CaptureSettings();
        settings.General ??= new GeneralSettings();
        settings.Logging ??= new LoggingSettings();
        settings.History ??= new HistorySettings();
        settings.Ipc ??= new IpcSettings();

        // FR-31: "start with Windows" became a default in version 2. A file from before that
        // omits the key, which would now read as the new default and switch it on for someone
        // who never asked; an upgrade keeps it off and leaves the choice in Settings.
        if (settings.Version < AppSettings.StartWithWindowsDefaultVersion)
        {
            settings.General.StartWithWindows = false;
            changes.Add("general.startWithWindows kept off: this settings file predates it being a default");
        }

        if (settings.Version < AppSettings.CurrentVersion)
        {
            settings.Version = AppSettings.CurrentVersion;
        }

        var d = settings.Downloads;
        d.Connections = Clamp(d.Connections, DownloadSettings.MinConnections, DownloadSettings.MaxConnections, "downloads.connections", changes);
        d.MaxRetries = Clamp(d.MaxRetries, 0, 100, "downloads.maxRetries", changes);
        d.ConnectTimeoutSeconds = Clamp(d.ConnectTimeoutSeconds, 1, 600, "downloads.connectTimeoutSeconds", changes);
        d.ReadIdleTimeoutSeconds = Clamp(d.ReadIdleTimeoutSeconds, 1, 3600, "downloads.readIdleTimeoutSeconds", changes);
        d.MaxConnectionsPerHost = Clamp(d.MaxConnectionsPerHost, 1, 64, "downloads.maxConnectionsPerHost", changes);
        settings.General.CompleteDialogHiddenCategories ??= [];
        d.AutoRetryAttempts = Clamp(d.AutoRetryAttempts, 0, 100, "downloads.autoRetryAttempts", changes);
        d.AutoRetryDelaySeconds = Clamp(d.AutoRetryDelaySeconds, 1, 86400, "downloads.autoRetryDelaySeconds", changes);
        if (string.IsNullOrWhiteSpace(d.UserAgent))
        {
            d.UserAgent = DownloadSettings.DefaultUserAgent;
            changes.Add("downloads.userAgent was empty; reset to default");
        }

        if (settings.SpeedLimits.GlobalBytesPerSecond is <= 0)
        {
            settings.SpeedLimits.GlobalBytesPerSecond = null;
            changes.Add("speedLimits.globalBytesPerSecond was not positive; set to unlimited");
        }

        // FR-9: a hand-edited window must not be able to leave the app throttled by a value it
        // cannot read, so anything unusable disables the window rather than applying blindly.
        settings.SpeedLimits.Window ??= new BandwidthWindowSettings();
        var w = settings.SpeedLimits.Window;
        w.Days ??= [];
        if (w.BytesPerSecond is <= 0)
        {
            w.BytesPerSecond = null;
            changes.Add("speedLimits.window.bytesPerSecond was not positive; set to unlimited");
        }

        if (w.Enabled && !TimeOnly.TryParse(w.Start, CultureInfo.InvariantCulture, out _))
        {
            w.Enabled = false;
            changes.Add($"speedLimits.window.start '{w.Start}' is not a time; window disabled");
        }

        if (w.Enabled && !string.IsNullOrWhiteSpace(w.Stop) && !TimeOnly.TryParse(w.Stop, CultureInfo.InvariantCulture, out _))
        {
            w.Enabled = false;
            changes.Add($"speedLimits.window.stop '{w.Stop}' is not a time; window disabled");
        }

        var c = settings.Capture;
        c.Extensions ??= [];
        c.ExcludedDomains ??= [];
        if (c.MinSizeBytes < 0)
        {
            c.MinSizeBytes = 0;
            changes.Add("capture.minSizeBytes was negative; set to 0");
        }

        var l = settings.Logging;
        if (l.FileSizeLimitBytes < LoggingSettings.MinFileSizeLimitBytes)
        {
            l.FileSizeLimitBytes = LoggingSettings.MinFileSizeLimitBytes;
            changes.Add($"logging.fileSizeLimitBytes raised to {LoggingSettings.MinFileSizeLimitBytes}");
        }

        l.RetainedDays = Clamp(l.RetainedDays, 1, 365, "logging.retainedDays", changes);
        if (!LogLevels.Contains(l.MinimumLevel, StringComparer.OrdinalIgnoreCase))
        {
            changes.Add($"logging.minimumLevel '{l.MinimumLevel}' is unknown; reset to Information");
            l.MinimumLevel = "Information";
        }

        if (settings.History.RetentionDays is <= 0)
        {
            settings.History.RetentionDays = null;
            changes.Add("history.retentionDays was not positive; history is kept indefinitely");
        }

        return changes;
    }

    private static int Clamp(int value, int min, int max, string name, List<string> changes)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            changes.Add($"{name} {value} is outside {min}–{max}; set to {clamped}");
        }

        return clamped;
    }
}
