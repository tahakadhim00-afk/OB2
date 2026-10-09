using System.Diagnostics;

namespace OB2.Core.Engine;

/// <summary>
/// Token-bucket rate limiter for bytes (FR-9). Allows a one-second burst, then paces callers
/// to the configured rate. Thread-safe; the rate can change while downloads run.
/// </summary>
public sealed class TokenBucket
{
    private readonly object _lock = new();
    private long? _bytesPerSecond;
    private double _tokens;
    private long _lastTicks = Stopwatch.GetTimestamp();

    public TokenBucket(long? bytesPerSecond = null)
    {
        BytesPerSecond = bytesPerSecond;
    }

    /// <summary>Null or non-positive means unlimited.</summary>
    public long? BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                return _bytesPerSecond;
            }
        }

        set
        {
            lock (_lock)
            {
                Refill();
                _bytesPerSecond = value is > 0 ? value : null;
                if (_bytesPerSecond is { } rate)
                {
                    _tokens = Math.Min(_tokens, rate);
                }
            }
        }
    }

    public bool IsLimited => BytesPerSecond is not null;

    /// <summary>A read size that keeps pacing smooth: about a tenth of a second of traffic.</summary>
    public int SuggestedChunk(int maximum)
    {
        var rate = BytesPerSecond;
        return rate is { } r ? (int)Math.Clamp(r / 10, 4096, maximum) : maximum;
    }

    /// <summary>Takes <paramref name="bytes"/> tokens, waiting first if the bucket is in debt.</summary>
    public ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_lock)
        {
            if (_bytesPerSecond is not { } rate)
            {
                return ValueTask.CompletedTask;
            }

            Refill();
            _tokens -= bytes;
            if (_tokens >= 0)
            {
                return ValueTask.CompletedTask;
            }

            delay = TimeSpan.FromSeconds(-_tokens / rate);
        }

        return new ValueTask(Task.Delay(delay, cancellationToken));
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds;
        _lastTicks = now;
        if (_bytesPerSecond is { } rate)
        {
            _tokens = Math.Min(rate, _tokens + (elapsed * rate));
        }
    }
}
