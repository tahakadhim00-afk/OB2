using Microsoft.Data.Sqlite;
using OB2.Storage.Database;
using OB2.Tests.Common;

namespace OB2.Storage.Tests.Database;

public sealed class MigrationRunnerTests : IDisposable
{
    private static readonly string[] SchemaV1Tables = ["categories", "queues", "downloads", "segments", "history_events", "schema_version"];

    private readonly TempDirectory _temp = new();
    private readonly SqliteConnectionFactory _factory;

    public MigrationRunnerTests()
    {
        _factory = new SqliteConnectionFactory(_temp.Combine("data", "ob2.db"));
        Directory.CreateDirectory(_temp.Combine("data"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void Applies_embedded_v1_schema_to_empty_database()
    {
        var result = new MigrationRunner(_factory).Run();

        result.StartingVersion.Should().Be(0);
        result.CurrentVersion.Should().Be(1);
        result.Applied.Should().Equal(1);

        Tables().Should().Contain(SchemaV1Tables);
        Indexes().Should().Contain(["idx_downloads_status", "idx_downloads_created"]);
        Columns("downloads").Should().Equal(
            "id", "url", "final_url", "filename", "dest_path", "category_id", "queue_id", "status",
            "total_bytes", "downloaded_bytes", "connections", "supports_ranges", "etag", "last_modified",
            "checksum_algo", "checksum_value", "referrer", "user_agent", "headers_json", "speed_limit_bps",
            "error_code", "error_message", "created_at", "started_at", "completed_at", "source");
        Columns("segments").Should().Equal(
            "download_id", "segment_index", "range_start", "range_end", "downloaded", "status", "retry_count");
    }

    [Fact]
    public void Rerun_is_idempotent()
    {
        new MigrationRunner(_factory).Run();
        var schemaBefore = SchemaSql();

        var second = new MigrationRunner(_factory).Run();
        var third = new MigrationRunner(_factory).Run();

        second.Applied.Should().BeEmpty();
        third.Applied.Should().BeEmpty();
        second.CurrentVersion.Should().Be(1);
        SchemaSql().Should().Be(schemaBefore);
        Scalar("SELECT COUNT(*) FROM schema_version").Should().Be(1);
    }

    [Fact]
    public void Records_version_name_checksum_and_time()
    {
        new MigrationRunner(_factory).Run();

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name, checksum, applied_at FROM schema_version";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt32(0).Should().Be(1);
        reader.GetString(1).Should().Be("init");
        reader.GetString(2).Should().MatchRegex("^[0-9A-F]{64}$");
        DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Applies_only_new_migrations_in_order()
    {
        new MigrationRunner(_factory, Source(M(1, "CREATE TABLE a (x INTEGER);"))).Run();

        var result = new MigrationRunner(_factory, Source(
            M(3, "ALTER TABLE a ADD COLUMN z INTEGER;"),
            M(1, "CREATE TABLE a (x INTEGER);"),
            M(2, "ALTER TABLE a ADD COLUMN y INTEGER;"))).Run();

        result.StartingVersion.Should().Be(1);
        result.Applied.Should().Equal(2, 3);
        result.CurrentVersion.Should().Be(3);
        Columns("a").Should().Equal("x", "y", "z");
    }

    [Fact]
    public void Failed_migration_rolls_back_and_is_not_recorded()
    {
        var source = Source(
            M(1, "CREATE TABLE a (x INTEGER);"),
            M(2, "CREATE TABLE b (y INTEGER); THIS IS NOT SQL;"));

        var act = () => new MigrationRunner(_factory, source).Run();

        act.Should().Throw<MigrationException>().WithMessage("*002_m2*");
        Tables().Should().Contain("a").And.NotContain("b");
        Scalar("SELECT MAX(version) FROM schema_version").Should().Be(1);
    }

    [Fact]
    public void Edited_migration_is_detected()
    {
        new MigrationRunner(_factory, Source(M(1, "CREATE TABLE a (x INTEGER);"))).Run();

        var act = () => new MigrationRunner(_factory, Source(M(1, "CREATE TABLE a (x INTEGER, y TEXT);"))).Run();

        act.Should().Throw<MigrationException>().WithMessage("*edited after it was applied*");
    }

    [Fact]
    public void Line_ending_differences_are_not_treated_as_edits()
    {
        new MigrationRunner(_factory, Source(M(1, "CREATE TABLE a (\n  x INTEGER\n);\n"))).Run();

        var act = () => new MigrationRunner(_factory, Source(M(1, "CREATE TABLE a (\r\n  x INTEGER\r\n);\r\n"))).Run();

        act.Should().NotThrow();
    }

    [Fact]
    public void Database_from_newer_build_is_rejected()
    {
        new MigrationRunner(_factory, Source(M(1, "CREATE TABLE a (x INTEGER);"), M(2, "CREATE TABLE b (x INTEGER);"))).Run();

        var act = () => new MigrationRunner(_factory, Source(M(1, "CREATE TABLE a (x INTEGER);"))).Run();

        act.Should().Throw<MigrationException>().WithMessage("*newer version*");
    }

    [Fact]
    public void Duplicate_versions_are_rejected()
    {
        var act = () => new MigrationRunner(_factory, Source(M(1, "SELECT 1;"), M(1, "SELECT 2;"))).Run();

        act.Should().Throw<MigrationException>().WithMessage("*Duplicate*");
    }

    [Fact]
    public void Embedded_source_finds_init_migration()
    {
        var migrations = EmbeddedMigrationSource.Instance.GetMigrations();

        migrations.Should().Contain(m => m.Version == 1 && m.Name == "init");
    }

    [Fact]
    public void Foreign_keys_are_enforced_and_segments_cascade()
    {
        new MigrationRunner(_factory).Run();
        Execute("""
            INSERT INTO downloads (id, url, filename, dest_path, status, created_at, source)
            VALUES (1, 'https://example.com/a.zip', 'a.zip', 'C:\a.zip', 'queued', '2026-10-10T00:00:00Z', 'manual');
            INSERT INTO segments (download_id, segment_index, range_start, range_end, status) VALUES (1, 0, 0, 99, 'pending');
            INSERT INTO history_events (download_id, event_type, occurred_at) VALUES (1, 'added', '2026-10-10T00:00:00Z');
            """);

        Execute("DELETE FROM downloads WHERE id = 1");

        Scalar("SELECT COUNT(*) FROM segments").Should().Be(0);
        Scalar("SELECT COUNT(*) FROM history_events WHERE download_id IS NULL").Should().Be(1);

        var orphan = () => Execute("INSERT INTO segments (download_id, segment_index, range_start, range_end, status) VALUES (99, 0, 0, 1, 'pending')");
        orphan.Should().Throw<SqliteException>().WithMessage("*FOREIGN KEY*");
    }

    [Fact]
    public void Database_uses_wal_journal()
    {
        new MigrationRunner(_factory).Run();

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        command.ExecuteScalar().Should().Be("wal");
    }

    private static Migration M(int version, string sql) => new(version, $"m{version}", sql);

    private static ListMigrationSource Source(params Migration[] migrations) => new(migrations);

    private sealed class ListMigrationSource(IReadOnlyList<Migration> migrations) : IMigrationSource
    {
        public IReadOnlyList<Migration> GetMigrations() => migrations;
    }

    private List<string> Tables() => Strings("SELECT name FROM sqlite_master WHERE type = 'table'");

    private List<string> Indexes() => Strings("SELECT name FROM sqlite_master WHERE type = 'index'");

    private List<string> Columns(string table) => Strings($"SELECT name FROM pragma_table_info('{table}') ORDER BY cid");

    private string SchemaSql() => string.Join("\n", Strings("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name"));

    private List<string> Strings(string sql)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private long Scalar(string sql)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
