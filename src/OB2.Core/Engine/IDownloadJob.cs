using OB2.Core.Downloads;

namespace OB2.Core.Engine;

/// <summary>
/// What the engine needs from a running download: the HTTP segment job for files, or the
/// yt-dlp job for video pages (D-71). Either one is the only writer of its row while it runs.
/// </summary>
internal interface IDownloadJob
{
    /// <summary>Ends with the final record once the job has paused, failed, or completed.</summary>
    Task<DownloadItem> Completion { get; }

    void Start();

    /// <summary>Requests a pause. Safe to call after the job has finished.</summary>
    void Cancel();

    DownloadProgress Snapshot();
}
