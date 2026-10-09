using OB2.Core.Downloads;
using OB2.Core.Integrity;

namespace OB2.Core.Engine;

/// <summary>What the user (or browser, or CLI) asked to download.</summary>
public sealed record DownloadRequest
{
    public required Uri Url { get; init; }

    public required string SaveFolder { get; init; }

    /// <summary>Exact name chosen by the user. Still sanitized.</summary>
    public string? FileName { get; init; }

    /// <summary>A name suggested by the browser; used when the server gives none.</summary>
    public string? FileNameHint { get; init; }

    public string? Referrer { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>Cookie header value. Kept in memory only (§11.1).</summary>
    public string? Cookies { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Null uses the engine default.</summary>
    public int? Connections { get; init; }

    public Checksum? Checksum { get; init; }

    public DownloadSource Source { get; init; } = DownloadSource.Manual;

    public long? QueueId { get; init; }

    public long? CategoryId { get; init; }

    public long? SpeedLimitBps { get; init; }
}
