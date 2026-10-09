using System.Net;
using OB2.Core.Http;

namespace OB2.Core.Engine;

/// <summary>
/// Exponential backoff for failed segments (FR-6): base × 2^(attempt−1), capped, with ±20 %
/// jitter so parallel segments do not retry in lockstep. Honours <c>Retry-After</c>.
/// </summary>
public sealed class RetryPolicy
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;

    public RetryPolicy(int maxRetries, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        MaxRetries = Math.Max(0, maxRetries);
        _baseDelay = baseDelay;
        _maxDelay = maxDelay;
    }

    /// <summary>Retries after the first attempt; 5 means up to 6 attempts.</summary>
    public int MaxRetries { get; }

    public static bool IsTransient(Exception ex) => ex switch
    {
        HttpStatusException status => status.IsTransient,
        TimeoutException => true,
        HttpRequestException => true,
        DownloadFailedException or RangeNotSupportedException => false,
        IOException => true, // network IOExceptions; disk errors are wrapped as DownloadFailedException
        _ => false,
    };

    /// <summary>True for errors that suggest the server dislikes the number of connections (§16.1).</summary>
    public static bool IsThrottling(Exception ex) => ex is HttpStatusException
    {
        StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable,
    };

    public TimeSpan DelayFor(int attempt, Exception? error = null)
    {
        if (error is HttpStatusException { RetryAfter: { } retryAfter })
        {
            return retryAfter < _maxDelay ? retryAfter : _maxDelay;
        }

        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var delay = _baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        delay = Math.Min(delay, _maxDelay.TotalMilliseconds);
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(delay * jitter);
    }
}
