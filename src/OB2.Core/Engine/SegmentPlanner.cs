using OB2.Core.Downloads;

namespace OB2.Core.Engine;

/// <summary>Splits a file into byte ranges (FR-3, FR-4).</summary>
public static class SegmentPlanner
{
    /// <summary>
    /// One open-ended or whole-file segment when ranges are unsupported or the size is
    /// unknown; otherwise up to <paramref name="connections"/> near-equal segments, none
    /// smaller than <paramref name="minSegmentBytes"/>.
    /// </summary>
    public static List<Segment> Plan(long? totalBytes, bool supportsRanges, int connections, long minSegmentBytes)
    {
        if (totalBytes is not { } total)
        {
            return [new Segment { Index = 0, Start = 0, End = Segment.OpenEnded }];
        }

        if (total == 0)
        {
            // Nothing to fetch; the job completes with an empty file.
            return [];
        }

        var count = supportsRanges
            ? (int)Math.Clamp(total / Math.Max(1, minSegmentBytes), 1, Math.Clamp(connections, 1, 32))
            : 1;

        var segments = new List<Segment>(count);
        var size = total / count;
        for (var i = 0; i < count; i++)
        {
            var start = i * size;
            var end = i == count - 1 ? total - 1 : start + size - 1;
            segments.Add(new Segment { Index = i, Start = start, End = end });
        }

        return segments;
    }
}
