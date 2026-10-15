namespace OB2.Core.Downloads;

/// <summary>One row of the <c>downloads</c> table (PRD §10.1).</summary>
public sealed class DownloadItem
{
    public const string PartSuffix = ".pgpart";

    public long Id { get; set; }

    public required string Url { get; set; }

    public string? FinalUrl { get; set; }

    public required string FileName { get; set; }

    /// <summary>Full path of the finished file. The partial file is this plus <see cref="PartSuffix"/>.</summary>
    public required string DestPath { get; set; }

    public long? CategoryId { get; set; }

    public long? QueueId { get; set; }

    /// <summary>Order inside the queue (FR-15). Lower runs first.</summary>
    public long QueuePosition { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    /// <summary>Null when the server did not report a size.</summary>
    public long? TotalBytes { get; set; }

    public long DownloadedBytes { get; set; }

    public int Connections { get; set; } = 1;

    public bool SupportsRanges { get; set; }

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    /// <summary>
    /// Hex SHA-256 of the first <see cref="HeadBytes"/> bytes of the entity, taken on the first
    /// transfer. Used only to decide whether a resume is safe when the server offers neither an
    /// ETag nor a Last-Modified (§10.3); null until known.
    /// </summary>
    public string? HeadSha256 { get; set; }

    /// <summary>How many leading bytes <see cref="HeadSha256"/> covers.</summary>
    public long? HeadBytes { get; set; }

    public string? ChecksumAlgo { get; set; }

    public string? ChecksumValue { get; set; }

    public string? Referrer { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Extra request headers. Never contains Cookie or Authorization (§11.1).</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-download limit in bytes per second; null is unlimited.</summary>
    public long? SpeedLimitBps { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DownloadSource Source { get; set; } = DownloadSource.Manual;

    /// <summary>
    /// A video page downloaded through yt-dlp (D-71): the format selector the user picked, for
    /// example <c>137+140</c> (video + audio). Null for an ordinary file.
    /// </summary>
    public string? MediaFormat { get; set; }

    /// <summary>What the parts are merged into (mp4, webm, mkv), or the single format's extension.</summary>
    public string? MediaContainer { get; set; }

    public bool IsMedia => MediaFormat is not null;

    public string PartPath => DestPath + PartSuffix;

    /// <summary>Where yt-dlp keeps the parts of a media download until they are merged.</summary>
    public string MediaPartsFolder => DestPath + ".parts";

    public string SaveFolder => Path.GetDirectoryName(DestPath) ?? string.Empty;

    public DownloadItem Clone()
    {
        var copy = (DownloadItem)MemberwiseClone();
        copy.Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase);
        return copy;
    }
}

/// <summary>One row of the <c>segments</c> table: a byte range fetched on its own connection.</summary>
public sealed class Segment
{
    /// <summary><see cref="End"/> value meaning "until the server closes the stream" (size unknown).</summary>
    public const long OpenEnded = -1;

    private long _downloaded;

    public int Index { get; init; }

    public long Start { get; init; }

    /// <summary>Inclusive end offset, or <see cref="OpenEnded"/>.</summary>
    public long End { get; set; }

    public long Downloaded
    {
        get => Volatile.Read(ref _downloaded);
        set => Volatile.Write(ref _downloaded, value);
    }

    public SegmentStatus Status { get; set; } = SegmentStatus.Pending;

    public int RetryCount { get; set; }

    public bool IsOpenEnded => End == OpenEnded;

    /// <summary>Length of the range, or null when open-ended.</summary>
    public long? Length => IsOpenEnded ? null : End - Start + 1;

    public long Position => Start + Downloaded;

    public bool IsComplete => Status == SegmentStatus.Done || (!IsOpenEnded && Downloaded >= Length);

    public void AddDownloaded(long bytes) => Interlocked.Add(ref _downloaded, bytes);

    public Segment Snapshot() => new()
    {
        Index = Index,
        Start = Start,
        End = End,
        Downloaded = Downloaded,
        Status = Status,
        RetryCount = RetryCount,
    };
}
