namespace OB2.Core.Downloads;

/// <summary>Download lifecycle states stored in <c>downloads.status</c> (PRD §10.1).</summary>
public enum DownloadStatus
{
    Queued,
    Probing,
    Downloading,
    Paused,
    Retrying,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Segment states stored in <c>segments.status</c>.</summary>
public enum SegmentStatus
{
    Pending,
    Active,
    Done,
    Failed,
}

/// <summary>Where a download came from (<c>downloads.source</c>).</summary>
public enum DownloadSource
{
    Manual,
    Chrome,
    Clipboard,
    Cli,
}

public static class DownloadStatusExtensions
{
    /// <summary>True while a job owns the download (it is being probed or transferred).</summary>
    public static bool IsRunning(this DownloadStatus status) =>
        status is DownloadStatus.Probing or DownloadStatus.Downloading or DownloadStatus.Retrying;

    public static bool IsFinished(this DownloadStatus status) =>
        status is DownloadStatus.Completed or DownloadStatus.Cancelled;

    /// <summary>The lower-case name used in the database and the IPC protocol.</summary>
    public static string ToDbString<T>(this T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    public static T ParseDbString<T>(string value)
        where T : struct, Enum => Enum.Parse<T>(value, ignoreCase: true);
}
