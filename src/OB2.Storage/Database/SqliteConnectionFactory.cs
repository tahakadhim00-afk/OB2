using Microsoft.Data.Sqlite;

namespace OB2.Storage.Database;

/// <summary>
/// Opens connections to the app database with the pragmas every connection needs:
/// foreign keys on, WAL journaling, and a busy timeout.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // WAL keeps the database consistent after a crash or power loss (NFR Reliability);
            // synchronous=NORMAL may lose the last commit on power loss, which the engine
            // reconciles against the partial file on startup (§11.3).
            command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
