using OB2.Core.Settings;

namespace OB2.Core.Engine;

/// <summary>Tunables for the download engine. Defaults follow the PRD.</summary>
public sealed record EngineOptions
{
    /// <summary>Connections per download (FR-3). Phase 2 always uses one.</summary>
    public int Connections { get; init; } = 8;

    /// <summary>Retries per segment before the download fails (FR-6).</summary>
    public int MaxRetries { get; init; } = 5;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest wait for the next bytes of a response (§11.3).</summary>
    public TimeSpan ReadIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public string UserAgent { get; init; } = DownloadSettings.DefaultUserAgent;

    /// <summary>Checkpoint at least this often while bytes are flowing (§11.3).</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>…or after this many new bytes, whichever comes first (§11.3).</summary>
    public long CheckpointBytes { get; init; } = 10L * 1024 * 1024;

    /// <summary>Read/write buffer per connection; NFR requires at least 64 KB.</summary>
    public int BufferSize { get; init; } = 256 * 1024;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound on simultaneous connections to one host across all downloads (§16.1).</summary>
    public int MaxConnectionsPerHost { get; init; } = 16;

    /// <summary>Segments are never smaller than this, so small files use fewer connections.</summary>
    public long MinSegmentBytes { get; init; } = 512 * 1024;

    public static EngineOptions FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var d = settings.Downloads;
        return new EngineOptions
        {
            Connections = d.Connections,
            MaxRetries = d.MaxRetries,
            ConnectTimeout = TimeSpan.FromSeconds(d.ConnectTimeoutSeconds),
            ReadIdleTimeout = TimeSpan.FromSeconds(d.ReadIdleTimeoutSeconds),
            UserAgent = d.UserAgent,
        };
    }
}
