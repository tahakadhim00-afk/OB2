using Microsoft.Extensions.Logging;
using OB2.Core.Settings;
using OB2.Core.Time;
using OB2.Storage.Database;
using OB2.Storage.Logging;
using OB2.Storage.Paths;
using OB2.Storage.Settings;
using Serilog.Extensions.Logging;

namespace OB2.Storage;

/// <summary>Everything the app needs from storage once startup has finished.</summary>
public sealed class StorageContext : IDisposable
{
    internal StorageContext(
        AppPaths paths,
        JsonSettingsStore settingsStore,
        AppSettings settings,
        SqliteConnectionFactory connectionFactory,
        ILoggerFactory loggerFactory,
        MigrationResult migration,
        InstanceLock instanceLock)
    {
        InstanceLock = instanceLock;
        Paths = paths;
        SettingsStore = settingsStore;
        Settings = settings;
        ConnectionFactory = connectionFactory;
        LoggerFactory = loggerFactory;
        Migration = migration;
    }

    public AppPaths Paths { get; }

    public JsonSettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public SqliteConnectionFactory ConnectionFactory { get; }

    public ILoggerFactory LoggerFactory { get; }

    public MigrationResult Migration { get; }

    /// <summary>Held for the process lifetime; only the first instance runs startup recovery.</summary>
    public InstanceLock InstanceLock { get; }

    /// <summary>Releases the instance lock and flushes and closes the log files.</summary>
    public void Dispose()
    {
        InstanceLock.Dispose();
        LoggerFactory.Dispose();
    }
}

/// <summary>
/// Startup sequence shared by the WPF app and the tests:
/// resolve paths → load settings → start logging → migrate the database.
/// </summary>
public static class StorageBootstrapper
{
    public static Task<StorageContext> InitializeAsync(
        AppPathEnvironment environment,
        IClock? clock = null,
        CancellationToken cancellationToken = default) =>
        InitializeAsync(AppPaths.Resolve(environment), clock, cancellationToken);

    public static async Task<StorageContext> InitializeAsync(
        AppPaths paths,
        IClock? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();

        // Settings come first because they configure logging; their issues are logged afterwards.
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        var load = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        var serilog = LogConfigurator.CreateLogger(paths.LogsDirectory, load.Settings.Logging);
        var loggerFactory = new SerilogLoggerFactory(serilog, dispose: true);
        try
        {
            var logger = loggerFactory.CreateLogger(typeof(StorageBootstrapper).FullName!);
            logger.LogInformation(
                "OB2 starting; data root {Root} ({Mode})",
                paths.Root,
                paths.IsPortable ? "portable" : "per-user");
            if (paths.FellBackFromReadOnly)
            {
                logger.LogWarning("portable.flag found but the program folder is read-only; using {Root} instead", paths.Root);
            }

            foreach (var issue in load.Issues)
            {
                logger.LogWarning("Settings: {Issue}", issue);
            }

            var connectionFactory = new SqliteConnectionFactory(paths.DatabaseFile);
            var migration = new MigrationRunner(
                connectionFactory,
                EmbeddedMigrationSource.Instance,
                clock,
                loggerFactory.CreateLogger<MigrationRunner>()).Run();

            return new StorageContext(paths, settingsStore, load.Settings, connectionFactory, loggerFactory, migration, InstanceLock.Acquire(paths));
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(typeof(StorageBootstrapper).FullName!).LogCritical(ex, "Startup failed");
            loggerFactory.Dispose();
            throw;
        }
    }
}
