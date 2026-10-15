using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OB2.Core.Downloads;
using OB2.Core.Time;
using OB2.Storage.Database;

namespace OB2.Storage.Repositories;

/// <summary>SQLite implementation of <see cref="IDownloadRepository"/> over the v1 schema.</summary>
public sealed class SqliteDownloadRepository : IDownloadRepository
{
    private const string Columns = """
        id, url, final_url, filename, dest_path, category_id, queue_id, status, total_bytes,
        downloaded_bytes, connections, supports_ranges, etag, last_modified, checksum_algo,
        checksum_value, referrer, user_agent, headers_json, speed_limit_bps, error_code,
        error_message, created_at, started_at, completed_at, source, queue_position, media_format,
        media_container
        """;

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqliteDownloadRepository(SqliteConnectionFactory factory, IClock? clock = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? SystemClock.Instance;
    }

    public long Insert(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO downloads (url, final_url, filename, dest_path, category_id, queue_id, status, total_bytes,
              downloaded_bytes, connections, supports_ranges, etag, last_modified, checksum_algo, checksum_value,
              referrer, user_agent, headers_json, speed_limit_bps, error_code, error_message, created_at,
              started_at, completed_at, source, queue_position, media_format, media_container)
            VALUES ($url, $final_url, $filename, $dest_path, $category_id, $queue_id, $status, $total_bytes,
              $downloaded_bytes, $connections, $supports_ranges, $etag, $last_modified, $checksum_algo, $checksum_value,
              $referrer, $user_agent, $headers_json, $speed_limit_bps, $error_code, $error_message, $created_at,
              $started_at, $completed_at, $source, $queue_position, $media_format, $media_container);
            SELECT last_insert_rowid();
            """;
        Bind(command, item);
        return (long)command.ExecuteScalar()!;
    }

    public void Update(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE downloads SET url = $url, final_url = $final_url, filename = $filename, dest_path = $dest_path,
              category_id = $category_id, queue_id = $queue_id, status = $status, total_bytes = $total_bytes,
              downloaded_bytes = $downloaded_bytes, connections = $connections, supports_ranges = $supports_ranges,
              etag = $etag, last_modified = $last_modified, checksum_algo = $checksum_algo,
              checksum_value = $checksum_value, referrer = $referrer, user_agent = $user_agent,
              headers_json = $headers_json, speed_limit_bps = $speed_limit_bps, error_code = $error_code,
              error_message = $error_message, created_at = $created_at, started_at = $started_at,
              completed_at = $completed_at, source = $source, queue_position = $queue_position,
              media_format = $media_format, media_container = $media_container
            WHERE id = $id
            """;
        Bind(command, item);
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();
    }

    public DownloadItem? Get(long id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM downloads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<DownloadItem> List()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM downloads ORDER BY id";
        using var reader = command.ExecuteReader();
        var items = new List<DownloadItem>();
        while (reader.Read())
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public void Delete(long id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM downloads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Segment> GetSegments(long downloadId)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment_index, range_start, range_end, downloaded, status, retry_count
            FROM segments WHERE download_id = $id ORDER BY segment_index
            """;
        command.Parameters.AddWithValue("$id", downloadId);
        using var reader = command.ExecuteReader();
        var segments = new List<Segment>();
        while (reader.Read())
        {
            segments.Add(new Segment
            {
                Index = reader.GetInt32(0),
                Start = reader.GetInt64(1),
                End = reader.GetInt64(2),
                Downloaded = reader.GetInt64(3),
                Status = DownloadStatusExtensions.ParseDbString<SegmentStatus>(reader.GetString(4)),
                RetryCount = reader.GetInt32(5),
            });
        }

        return segments;
    }

    public void SaveSegments(long downloadId, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM segments WHERE download_id = $id";
            delete.Parameters.AddWithValue("$id", downloadId);
            delete.ExecuteNonQuery();
        }

        UpsertSegments(connection, transaction, downloadId, segments);
        transaction.Commit();
    }

    public void Checkpoint(long downloadId, long downloadedBytes, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE downloads SET downloaded_bytes = $bytes WHERE id = $id";
            update.Parameters.AddWithValue("$bytes", downloadedBytes);
            update.Parameters.AddWithValue("$id", downloadId);
            update.ExecuteNonQuery();
        }

        UpsertSegments(connection, transaction, downloadId, segments);
        transaction.Commit();
    }

    public void AddHistoryEvent(long? downloadId, string eventType, string? detail = null)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO history_events (download_id, event_type, detail, occurred_at) VALUES ($id, $type, $detail, $at)";
        command.Parameters.AddWithValue("$id", (object?)downloadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", Format(_clock.UtcNow));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryEvent> GetHistory(long downloadId)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, download_id, event_type, detail, occurred_at FROM history_events WHERE download_id = $id ORDER BY id";
        command.Parameters.AddWithValue("$id", downloadId);
        using var reader = command.ExecuteReader();
        var events = new List<HistoryEvent>();
        while (reader.Read())
        {
            events.Add(new HistoryEvent(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Parse(reader.GetString(4))));
        }

        return events;
    }

    public int PruneHistory(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = Format(_clock.UtcNow.AddDays(-retentionDays));
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();

        // An event is only dropped once nothing still points at it: either it was already
        // orphaned (its download was deleted, leaving download_id NULL via ON DELETE SET NULL)
        // or its download has finished. A paused or running download keeps its whole trail so
        // the detail panel does not lose the reason it stopped.
        command.CommandText = """
            DELETE FROM history_events
            WHERE occurred_at < $cutoff
              AND (download_id IS NULL
                   OR download_id IN (SELECT id FROM downloads WHERE status IN ('completed', 'failed', 'cancelled')))
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<long> MarkInterruptedAsPaused()
    {
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        var ids = new List<long>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM downloads WHERE status IN ('probing', 'downloading', 'retrying')";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE downloads SET status = 'paused' WHERE status IN ('probing', 'downloading', 'retrying');
                UPDATE segments SET status = 'pending' WHERE status = 'active';
                """;
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return ids;
    }

    private static void UpsertSegments(SqliteConnection connection, SqliteTransaction transaction, long downloadId, IReadOnlyList<Segment> segments)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO segments (download_id, segment_index, range_start, range_end, downloaded, status, retry_count)
            VALUES ($id, $index, $start, $end, $downloaded, $status, $retries)
            ON CONFLICT (download_id, segment_index) DO UPDATE SET
              range_start = excluded.range_start, range_end = excluded.range_end, downloaded = excluded.downloaded,
              status = excluded.status, retry_count = excluded.retry_count
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var index = command.Parameters.Add("$index", SqliteType.Integer);
        var start = command.Parameters.Add("$start", SqliteType.Integer);
        var end = command.Parameters.Add("$end", SqliteType.Integer);
        var downloaded = command.Parameters.Add("$downloaded", SqliteType.Integer);
        var status = command.Parameters.Add("$status", SqliteType.Text);
        var retries = command.Parameters.Add("$retries", SqliteType.Integer);
        id.Value = downloadId;

        foreach (var segment in segments)
        {
            index.Value = segment.Index;
            start.Value = segment.Start;
            end.Value = segment.End;
            downloaded.Value = segment.Downloaded;
            status.Value = segment.Status.ToDbString();
            retries.Value = segment.RetryCount;
            command.ExecuteNonQuery();
        }
    }

    private static void Bind(SqliteCommand command, DownloadItem item)
    {
        var p = command.Parameters;
        p.AddWithValue("$url", item.Url);
        p.AddWithValue("$final_url", Db(item.FinalUrl));
        p.AddWithValue("$filename", item.FileName);
        p.AddWithValue("$dest_path", item.DestPath);
        p.AddWithValue("$category_id", Db(item.CategoryId));
        p.AddWithValue("$queue_id", Db(item.QueueId));
        p.AddWithValue("$status", item.Status.ToDbString());
        p.AddWithValue("$total_bytes", Db(item.TotalBytes));
        p.AddWithValue("$downloaded_bytes", item.DownloadedBytes);
        p.AddWithValue("$connections", item.Connections);
        p.AddWithValue("$supports_ranges", item.SupportsRanges ? 1 : 0);
        p.AddWithValue("$etag", Db(item.ETag));
        p.AddWithValue("$last_modified", Db(item.LastModified));
        p.AddWithValue("$checksum_algo", Db(item.ChecksumAlgo));
        p.AddWithValue("$checksum_value", Db(item.ChecksumValue));
        p.AddWithValue("$referrer", Db(item.Referrer));
        p.AddWithValue("$user_agent", Db(item.UserAgent));
        p.AddWithValue("$headers_json", item.Headers.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(item.Headers));
        p.AddWithValue("$speed_limit_bps", Db(item.SpeedLimitBps));
        p.AddWithValue("$error_code", Db(item.ErrorCode));
        p.AddWithValue("$error_message", Db(item.ErrorMessage));
        p.AddWithValue("$created_at", Format(item.CreatedAt));
        p.AddWithValue("$started_at", item.StartedAt is { } s ? Format(s) : DBNull.Value);
        p.AddWithValue("$completed_at", item.CompletedAt is { } c ? Format(c) : DBNull.Value);
        p.AddWithValue("$source", item.Source.ToDbString());
        p.AddWithValue("$queue_position", item.QueuePosition);
        p.AddWithValue("$media_format", Db(item.MediaFormat));
        p.AddWithValue("$media_container", Db(item.MediaContainer));
    }

    private static DownloadItem Read(SqliteDataReader r)
    {
        var headers = r.IsDBNull(18) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(18));
        return new DownloadItem
        {
            Id = r.GetInt64(0),
            Url = r.GetString(1),
            FinalUrl = Str(r, 2),
            FileName = r.GetString(3),
            DestPath = r.GetString(4),
            CategoryId = Long(r, 5),
            QueueId = Long(r, 6),
            Status = DownloadStatusExtensions.ParseDbString<DownloadStatus>(r.GetString(7)),
            TotalBytes = Long(r, 8),
            DownloadedBytes = r.GetInt64(9),
            Connections = r.GetInt32(10),
            SupportsRanges = r.GetInt64(11) != 0,
            ETag = Str(r, 12),
            LastModified = Str(r, 13),
            ChecksumAlgo = Str(r, 14),
            ChecksumValue = Str(r, 15),
            Referrer = Str(r, 16),
            UserAgent = Str(r, 17),
            Headers = headers is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            SpeedLimitBps = Long(r, 19),
            ErrorCode = Str(r, 20),
            ErrorMessage = Str(r, 21),
            CreatedAt = Parse(r.GetString(22)),
            StartedAt = r.IsDBNull(23) ? null : Parse(r.GetString(23)),
            CompletedAt = r.IsDBNull(24) ? null : Parse(r.GetString(24)),
            Source = DownloadStatusExtensions.ParseDbString<DownloadSource>(r.GetString(25)),
            QueuePosition = r.GetInt64(26),
            MediaFormat = Str(r, 27),
            MediaContainer = Str(r, 28),
        };
    }

    private static object Db(string? value) => (object?)value ?? DBNull.Value;

    private static object Db(long? value) => value is { } v ? v : DBNull.Value;

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    internal static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
