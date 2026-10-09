using System.Diagnostics;
using OB2.Core.Downloads;

namespace OB2.Core.Engine;

/// <summary>A point-in-time view of a running download for the UI and IPC (4 Hz polling).</summary>
public sealed record DownloadProgress(
    long DownloadId,
    DownloadStatus Status,
    long? TotalBytes,
    long DownloadedBytes,
    double BytesPerSecond,
    int ActiveConnections,
    IReadOnlyList<Segment> Segments)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Min(1.0, (double)DownloadedBytes / TotalBytes.Value) : null;

    public TimeSpan? Remaining =>
        TotalBytes is { } total && BytesPerSecond > 1
            ? TimeSpan.FromSeconds(Math.Max(0, total - DownloadedBytes) / BytesPerSecond)
            : null;
}

/// <summary>Average transfer speed over a sliding window of samples.</summary>
internal sealed class SpeedMeter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private readonly object _lock = new();

    public void Sample(long totalBytes)
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            _samples.Enqueue((now, totalBytes));
            while (_samples.Count > 2 && Stopwatch.GetElapsedTime(_samples.Peek().Ticks, now) > Window)
            {
                _samples.Dequeue();
            }
        }
    }

    public double BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count < 2)
                {
                    return 0;
                }

                var first = _samples.Peek();
                var last = _samples.Last();
                var seconds = Stopwatch.GetElapsedTime(first.Ticks, last.Ticks).TotalSeconds;
                return seconds <= 0 ? 0 : (last.Bytes - first.Bytes) / seconds;
            }
        }
    }
}
