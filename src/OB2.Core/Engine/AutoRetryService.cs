using Microsoft.Extensions.Logging;
using OB2.Core.Downloads;
using OB2.Core.Settings;

namespace OB2.Core.Engine;

/// <summary>
/// Starts a failed download again after a growing delay (FR-6, backlog "overnight queue"). The
/// retries inside one attempt cover a connection dropping mid-transfer; this covers the line
/// being down for minutes, so a queue left running overnight survives a brief outage instead of
/// stopping at the first failure and waiting for someone to press Retry.
/// <para>
/// Only failures that another attempt could plausibly fix are retried. A checksum mismatch, a
/// full disk, an unsafe path or a 4xx will fail again the same way, and retrying those forever
/// would waste the user's bandwidth and bury the real error.
/// </para>
/// </summary>
public sealed class AutoRetryService : IDisposable
{
    private readonly Dictionary<long, int> _attempts = [];
    private readonly Dictionary<long, CancellationTokenSource> _pending = [];
    private readonly object _lock = new();
    private readonly Func<DownloadSettings> _settings;
    private readonly Action<long> _start;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private bool _disposed;

    public AutoRetryService(Func<DownloadSettings> settings, Action<long> start, ILogger? logger = null, TimeProvider? time = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Error codes that another attempt cannot fix, so they are never retried.</summary>
    public static bool IsWorthRetrying(string? errorCode) => errorCode switch
    {
        null => false,
        ErrorCodes.Network or ErrorCodes.Timeout => true,
        ErrorCodes.ChecksumMismatch or ErrorCodes.SizeMismatch => false,
        ErrorCodes.DiskFull or ErrorCodes.FileError or ErrorCodes.UnsafePath => false,
        ErrorCodes.RangeNotSupported or ErrorCodes.Internal => false,
        ErrorCodes.ToolsMissing or ErrorCodes.SiteError => false,
        // HTTP_nnn: the server's own 5xx and the handful of transient 4xx are worth another go.
        _ => IsTransientHttp(errorCode),
    };

    /// <summary>Counts an attempt and schedules the next start, or gives up.</summary>
    public void OnFailed(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var settings = _settings();
        if (settings.AutoRetryAttempts <= 0 || !IsWorthRetrying(item.ErrorCode))
        {
            return;
        }

        var delay = TimeSpan.Zero;
        int attempt;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var used = _attempts.GetValueOrDefault(item.Id);
            if (used >= settings.AutoRetryAttempts)
            {
                _logger?.LogInformation(
                    "Auto-retry gave up on {Id} after {Attempts} attempts ({Code})", item.Id, used, item.ErrorCode);
                return;
            }

            attempt = used + 1;
            _attempts[item.Id] = attempt;

            // Doubling, capped at an hour so a long outage does not push the next try past the
            // night. The first retry waits the configured delay, not zero.
            var seconds = Math.Max(1, settings.AutoRetryDelaySeconds) * Math.Pow(2, used);
            delay = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromHours(1).TotalSeconds));
            CancelPending(item.Id);
            _pending[item.Id] = new CancellationTokenSource();
        }

        _logger?.LogInformation(
            "Auto-retry {Attempt} of {Max} for {Id} in {Delay:0}s ({Code})",
            attempt, settings.AutoRetryAttempts, item.Id, delay.TotalSeconds, item.ErrorCode);
        _ = RetryAfterAsync(item.Id, delay);
    }

    /// <summary>
    /// Clears the count for a download, so a success or a deliberate action starts the budget
    /// fresh rather than letting one bad night exhaust it forever.
    /// </summary>
    public void Reset(long id)
    {
        lock (_lock)
        {
            _attempts.Remove(id);
            CancelPending(id);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var pending in _pending.Values)
            {
                pending.Cancel();
                pending.Dispose();
            }

            _pending.Clear();
            _attempts.Clear();
        }
    }

    private static bool IsTransientHttp(string errorCode)
    {
        if (!errorCode.StartsWith("HTTP_", StringComparison.Ordinal)
            || !int.TryParse(errorCode.AsSpan(5), out var status))
        {
            return false;
        }

        // 408 request timeout, 425 too early, 429 too many requests, and every 5xx.
        return status is 408 or 425 or 429 || status >= 500;
    }

    private async Task RetryAfterAsync(long id, TimeSpan delay)
    {
        CancellationToken token;
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out var source))
            {
                return;
            }

            token = source.Token;
        }

        try
        {
            await Task.Delay(delay, _time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // reset, or shutting down
        }

        lock (_lock)
        {
            if (_disposed || !_pending.Remove(id, out var source))
            {
                return;
            }

            source.Dispose();
        }

        try
        {
            _start(id);
        }
        catch (Exception ex)
        {
            // The engine refusing one start must not take down the timer for everything else.
            _logger?.LogWarning(ex, "Auto-retry could not start {Id}", id);
        }
    }

    private void CancelPending(long id)
    {
        if (_pending.Remove(id, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }
    }
}
