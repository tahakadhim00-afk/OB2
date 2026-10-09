using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OB2.Core.Time;

namespace OB2.Storage.Database;

/// <summary>Result of a migration run.</summary>
public sealed record MigrationResult(int StartingVersion, int CurrentVersion, IReadOnlyList<int> Applied);

/// <summary>
/// Applies numbered migrations in order and records each in <c>schema_version</c> (PRD §10.1).
/// Each migration runs in its own transaction together with its version row, so a failure
/// leaves the database at the last good version. Re-running is a no-op.
/// </summary>
public sealed class MigrationRunner
{
    private const string CreateVersionTableSql = """
        CREATE TABLE IF NOT EXISTS schema_version (
          version     INTEGER PRIMARY KEY,
          name        TEXT NOT NULL,
          checksum    TEXT NOT NULL,
          applied_at  TEXT NOT NULL
        );
        """;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IMigrationSource _source;
    private readonly IClock _clock;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(
        SqliteConnectionFactory connectionFactory,
        IMigrationSource? source = null,
        IClock? clock = null,
        ILogger<MigrationRunner>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _source = source ?? EmbeddedMigrationSource.Instance;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullLogger<MigrationRunner>.Instance;
    }

    public MigrationResult Run()
    {
        var migrations = LoadOrdered();

        using var connection = _connectionFactory.Open();
        Execute(connection, null, CreateVersionTableSql);

        var applied = ReadApplied(connection);
        VerifyApplied(migrations, applied);

        var startingVersion = applied.Count == 0 ? 0 : applied.Keys.Max();
        var newlyApplied = new List<int>();

        foreach (var migration in migrations)
        {
            if (applied.ContainsKey(migration.Version))
            {
                continue;
            }

            if (Apply(connection, migration))
            {
                newlyApplied.Add(migration.Version);
            }
        }

        var currentVersion = newlyApplied.Count == 0 ? startingVersion : Math.Max(startingVersion, newlyApplied.Max());
        if (newlyApplied.Count == 0)
        {
            _logger.LogInformation("Database schema is current at version {Version}", currentVersion);
        }
        else
        {
            _logger.LogInformation("Database migrated from version {From} to {To}", startingVersion, currentVersion);
        }

        return new MigrationResult(startingVersion, currentVersion, newlyApplied);
    }

    private List<Migration> LoadOrdered()
    {
        var migrations = _source.GetMigrations().OrderBy(m => m.Version).ToList();

        var duplicate = migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new MigrationException($"Duplicate migration version {duplicate.Key:D3}: {string.Join(", ", duplicate.Select(m => m.Name))}.");
        }

        if (migrations.Any(m => m.Version <= 0))
        {
            throw new MigrationException("Migration versions must start at 001.");
        }

        return migrations;
    }

    private static void VerifyApplied(List<Migration> migrations, Dictionary<int, string> applied)
    {
        var known = migrations.ToDictionary(m => m.Version);
        foreach (var (version, checksum) in applied)
        {
            if (!known.TryGetValue(version, out var migration))
            {
                throw new MigrationException(
                    $"The database has migration {version:D3}, which this build does not know. It was probably created by a newer version of OB2.");
            }

            if (!string.Equals(migration.Checksum, checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationException(
                    $"Migration {version:D3}_{migration.Name} was edited after it was applied. Add a new migration instead of changing an applied one.");
            }
        }
    }

    /// <returns>False if another process applied the migration first.</returns>
    private bool Apply(SqliteConnection connection, Migration migration)
    {
        _logger.LogInformation("Applying migration {Version:D3}_{Name}", migration.Version, migration.Name);

        // BeginTransaction() issues BEGIN IMMEDIATE, so a second process cannot migrate concurrently.
        using var transaction = connection.BeginTransaction();
        try
        {
            if (ReadApplied(connection, transaction).ContainsKey(migration.Version))
            {
                transaction.Rollback();
                return false;
            }

            Execute(connection, transaction, migration.Sql);

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO schema_version (version, name, checksum, applied_at) VALUES ($v, $n, $c, $t)";
            insert.Parameters.AddWithValue("$v", migration.Version);
            insert.Parameters.AddWithValue("$n", migration.Name);
            insert.Parameters.AddWithValue("$c", migration.Checksum);
            insert.Parameters.AddWithValue("$t", _clock.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();

            transaction.Commit();
            return true;
        }
        catch (Exception ex) when (ex is not MigrationException)
        {
            transaction.Rollback();
            throw new MigrationException($"Migration {migration.Version:D3}_{migration.Name} failed: {ex.Message}", ex);
        }
    }

    private static Dictionary<int, string> ReadApplied(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, checksum FROM schema_version";
        using var reader = command.ExecuteReader();

        var applied = new Dictionary<int, string>();
        while (reader.Read())
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
