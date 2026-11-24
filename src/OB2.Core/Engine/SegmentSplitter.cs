using OB2.Core.Downloads;

namespace OB2.Core.Engine;

/// <summary>
/// D-90: keeps every connection busy to the end. A connection with nothing left to do takes over
/// the second half of the segment with the most bytes still to come, so a download never finishes
/// on one or two connections while the others sit idle (and a resumed download with few unfinished
/// segments gets all its connections back). The caller holds the job's split lock, under which
/// connections also reserve the bytes they are writing, so a split never cuts into them.
/// </summary>
public static class SegmentSplitter
{
    /// <summary>
    /// Shortens the segment with the most bytes left and returns a new segment for the rest of
    /// its range, or null when no segment has enough left for two pieces of at least
    /// <paramref name="minPieceBytes"/>. The two together cover exactly the old range; the donor
    /// keeps the bytes it is writing and at least half of what it had left.
    /// </summary>
    /// <param name="nextIndex">The new segment's index: one more than any in the download.</param>
    public static Segment? TrySplit(IReadOnlyList<Segment> segments, long minPieceBytes, int nextIndex)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentOutOfRangeException.ThrowIfLessThan(minPieceBytes, 1);

        Segment? donor = null;
        long most = 0;
        foreach (var segment in segments)
        {
            if (segment.IsOpenEnded || segment.IsComplete)
            {
                continue; // an unknown end cannot be halved
            }

            var left = Left(segment);
            if (left > most)
            {
                most = left;
                donor = segment;
            }
        }

        if (donor is null)
        {
            return null;
        }

        var keep = Math.Max(most - (most / 2), minPieceBytes); // the donor keeps the larger half
        var piece = most - keep;
        if (piece < minPieceBytes)
        {
            return null;
        }

        var start = donor.Position + donor.Reserved + keep;
        var split = new Segment { Index = nextIndex, Start = start, End = donor.End };
        donor.End = start - 1;
        return split;
    }

    /// <summary>Bytes of the segment not yet written or being written.</summary>
    public static long Left(Segment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.End - (segment.Position + segment.Reserved) + 1;
    }
}
