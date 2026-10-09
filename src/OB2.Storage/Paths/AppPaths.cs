namespace OB2.Storage.Paths;

/// <summary>The machine-specific inputs to path resolution, injectable for tests.</summary>
public sealed record AppPathEnvironment(string BaseDirectory, string AppDataDirectory)
{
    public static AppPathEnvironment Current { get; } = new(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
}

/// <summary>
/// Where settings, the database, and logs live (PRD §10.2). With <c>portable.flag</c> next to
/// the executable everything sits beside it; otherwise under <c>%APPDATA%\OB2</c>.
/// </summary>
public sealed record AppPaths(string Root, bool IsPortable)
{
    public const string PortableFlagFileName = "portable.flag";
    public const string AppFolderName = "OB2";

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string DataDirectory => Path.Combine(Root, "data");

    public string DatabaseFile => Path.Combine(DataDirectory, "ob2.db");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static AppPaths Resolve(AppPathEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var baseDirectory = Path.GetFullPath(environment.BaseDirectory);
        if (File.Exists(Path.Combine(baseDirectory, PortableFlagFileName)))
        {
            return new AppPaths(Path.TrimEndingDirectorySeparator(baseDirectory), IsPortable: true);
        }

        return new AppPaths(Path.Combine(Path.GetFullPath(environment.AppDataDirectory), AppFolderName), IsPortable: false);
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
