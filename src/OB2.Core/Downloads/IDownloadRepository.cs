namespace OB2.Core.Downloads;

/// <summary>
/// Persistence for downloads, segments, and history. Implementations must be safe to call
/// from several threads; each call is its own unit of work.
/// </summary>
public interface IDownloadRepository
{
    long Insert(DownloadItem item);

    /// <summary>Writes every column of the row.</summary>
    void Update(DownloadItem item);

    DownloadItem? Get(long id);

    IReadOnlyList<DownloadItem> List();

    void Delete(long id);

    IReadOnlyList<Segment> GetSegments(long downloadId);

    /// <summary>Replaces all segments of a download.</summary>
    void SaveSegments(long downloadId, IReadOnlyList<Segment> segments);

    /// <summary>
    /// Records progress atomically: the download's byte count and every segment's state in
    /// one transaction (§11.3). Callers flush file data to disk before calling this.
    /// </summary>
    void Checkpoint(long downloadId, long downloadedBytes, IReadOnlyList<Segment> segments);

    void AddHistoryEvent(long? downloadId, string eventType, string? detail = null);

    IReadOnlyList<HistoryEvent> GetHistory(long downloadId);

    /// <summary>
    /// §10.4: drops history older than <paramref name="retentionDays"/>, and with it the rows of
    /// finished downloads that are no longer referenced. Returns how many events were removed.
    /// Events belonging to a download that is still listed are kept however old they are, so a
    /// long-running or paused download never loses its own trail.
    /// </summary>
    int PruneHistory(int retentionDays);

    /// <summary>
    /// Startup recovery (§11.3): downloads left probing/downloading/retrying by a crash
    /// become paused. Returns their ids.
    /// </summary>
    IReadOnlyList<long> MarkInterruptedAsPaused();
}

public sealed record HistoryEvent(long Id, long? DownloadId, string EventType, string? Detail, DateTimeOffset OccurredAt);
