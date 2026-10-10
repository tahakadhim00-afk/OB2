namespace OB2.Storage.Paths;

/// <summary>The machine-specific inputs to path resolution, injectable for tests.</summary>
public sealed record AppPathEnvironment(string BaseDirectory, string AppDataDirectory)
{
    public static AppPathEnvironment Current { get; } = new(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>Whether a folder accepts new files. Replaceable in tests.</summary>
    public Func<string, bool> IsWritable { get; init; } = ProbeWritable;

    private static bool ProbeWritable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".ob2-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Where settings, the database, and logs live (PRD §10.2). With <c>portable.flag</c> next to
/// the executable everything sits beside it; otherwise under <c>%APPDATA%\OB2</c>. A portable
/// copy on a read-only medium falls back to <c>%APPDATA%\OB2</c> (§12.3).
/// </summary>
public sealed record AppPaths(string Root, bool IsPortable)
{
    public const string PortableFlagFileName = "portable.flag";
    public const string AppFolderName = "OB2";

    /// <summary>True when <c>portable.flag</c> was found but the folder was read-only.</summary>
    public bool FellBackFromReadOnly { get; init; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string DataDirectory => Path.Combine(Root, "data");

    public string DatabaseFile => Path.Combine(DataDirectory, "ob2.db");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static AppPaths Resolve(AppPathEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var baseDirectory = Path.GetFullPath(environment.BaseDirectory);
        var perUser = Path.Combine(Path.GetFullPath(environment.AppDataDirectory), AppFolderName);
        if (File.Exists(Path.Combine(baseDirectory, PortableFlagFileName)))
        {
            return environment.IsWritable(baseDirectory)
                ? new AppPaths(Path.TrimEndingDirectorySeparator(baseDirectory), IsPortable: true)
                : new AppPaths(perUser, IsPortable: false) { FellBackFromReadOnly = true };
        }

        return new AppPaths(perUser, IsPortable: false);
    }

    /// <summary>An explicit data root (the <c>--data</c> option), laid out like a portable install.</summary>
    public static AppPaths ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return new AppPaths(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), IsPortable: true);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
