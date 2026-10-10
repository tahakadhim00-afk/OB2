namespace OB2.Core.Settings;

/// <summary>
/// User settings persisted as <c>settings.json</c> (PRD §10.2). Defaults come from the PRD;
/// values marked "(decide)" there are recorded in docs/decisions.md.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    /// <summary>Format version of the settings file, for future migrations.</summary>
    public int Version { get; set; } = CurrentVersion;

    public DownloadSettings Downloads { get; set; } = new();

    public FolderSettings Folders { get; set; } = new();

    public SpeedLimitSettings SpeedLimits { get; set; } = new();

    public CaptureSettings Capture { get; set; } = new();

    public GeneralSettings General { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();

    public HistorySettings History { get; set; } = new();

    public IpcSettings Ipc { get; set; } = new();
}

/// <summary>§11.1: the app/host pipe name carries a per-install random suffix.</summary>
public sealed class IpcSettings
{
    public const string DefaultPrefix = "OB2-";

    /// <summary>Generated on first start of the app; read by host.exe from settings.json.</summary>
    public string? PipeName { get; set; }

    public static string NewPipeName() => DefaultPrefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}

/// <summary>FR-3, FR-6, FR-30, §11.3.</summary>
public sealed class DownloadSettings
{
    public const int MinConnections = 1;
    public const int MaxConnections = 32;

    public int Connections { get; set; } = 8;

    public int MaxRetries { get; set; } = 5;

    public int ConnectTimeoutSeconds { get; set; } = 30;

    public int ReadIdleTimeoutSeconds { get; set; } = 60;

    public string UserAgent { get; set; } = DefaultUserAgent;

    /// <summary>Cap on connections to one server across all downloads (§16.1).</summary>
    public int MaxConnectionsPerHost { get; set; } = 16;

    /// <summary>§11.3 / open question 3: interrupted downloads are not auto-resumed by default.</summary>
    public bool AutoResumeOnStartup { get; set; }

    /// <summary>§11.1: cookies are memory-only unless the user opts in.</summary>
    public bool RememberAuth { get; set; }

    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
}

/// <summary>FR-29. Per-category folders live in the <c>categories</c> table, not here.</summary>
public sealed class FolderSettings
{
    /// <summary>Null until the first-run wizard (Phase 5) or the user sets it.</summary>
    public string? DefaultSaveFolder { get; set; }
}

/// <summary>FR-9.</summary>
public sealed class SpeedLimitSettings
{
    /// <summary>Global limit in bytes per second; null means unlimited.</summary>
    public long? GlobalBytesPerSecond { get; set; }
}

/// <summary>§8.4.</summary>
public sealed class CaptureSettings
{
    public long MinSizeBytes { get; set; } = 1024 * 1024;

    public List<string> Extensions { get; set; } =
    [
        "7z", "apk", "avi", "bin", "bz2", "deb", "dmg", "doc", "docx", "exe", "flac", "gz", "img",
        "iso", "m4a", "mkv", "mov", "mp3", "mp4", "msi", "pdf", "ppt", "pptx", "rar", "tar", "wav",
        "webm", "xls", "xlsx", "xz", "zip",
    ];

    public List<string> ExcludedDomains { get; set; } = [];

    /// <summary>FR-22: show the confirmation dialog before a captured download starts.</summary>
    public bool ConfirmBeforeAdding { get; set; } = true;
}

/// <summary>§9.4, FR-31.</summary>
public sealed class GeneralSettings
{
    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool StartWithWindows { get; set; }

    /// <summary>§9.3 Download Complete dialog.</summary>
    public bool ShowCompleteDialog { get; set; } = true;

    /// <summary>Categories for which the user ticked "Do not show again for this category".</summary>
    public List<string> CompleteDialogHiddenCategories { get; set; } = [];

    /// <summary>FR-27 Windows notification on completion or failure.</summary>
    public bool ShowNotifications { get; set; } = true;
}

/// <summary>NFR Logging: rolling files, 7 days, 10 MB max each.</summary>
public sealed class LoggingSettings
{
    public const long MinFileSizeLimitBytes = 1024;

    public string MinimumLevel { get; set; } = "Information";

    public long FileSizeLimitBytes { get; set; } = 10L * 1024 * 1024;

    public int RetainedDays { get; set; } = 7;
}

/// <summary>§10.4.</summary>
public sealed class HistorySettings
{
    /// <summary>Null keeps history indefinitely.</summary>
    public int? RetentionDays { get; set; }
}
