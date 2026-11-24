using OB2.Core.Downloads;
using OB2.Core.Engine;

namespace OB2.Core.Tests.Engine;

/// <summary>D-90: an idle connection takes over half of the segment with the most bytes left.</summary>
public sealed class SegmentSplitterTests
{
    private const long MB = 1024 * 1024;

    private static Segment Seg(int index, long start, long end, long downloaded = 0, long reserved = 0) =>
        new() { Index = index, Start = start, End = end, Downloaded = downloaded, Reserved = reserved };

    [Fact]
    public void The_segment_with_the_most_left_gives_away_its_second_half()
    {
        var segments = new List<Segment>
        {
            Seg(0, 0, 10 * MB - 1, downloaded: 9 * MB),        // 1 MB left
            Seg(1, 10 * MB, 20 * MB - 1, downloaded: 2 * MB),  // 8 MB left: the donor
            Seg(2, 20 * MB, 30 * MB - 1, downloaded: 5 * MB),  // 5 MB left
        };

        var piece = SegmentSplitter.TrySplit(segments, 1 * MB, nextIndex: 3);

        piece.Should().NotBeNull();
        piece!.Index.Should().Be(3);
        piece.Start.Should().Be(16 * MB, "the donor at 12 MB keeps 4 of its 8 MB");
        piece.End.Should().Be(20 * MB - 1, "the piece ends where the donor used to");
        segments[1].End.Should().Be(16 * MB - 1);
        segments[0].End.Should().Be(10 * MB - 1, "the others are untouched");
    }

    [Fact]
    public void The_donor_and_the_piece_together_cover_exactly_the_old_range()
    {
        var donor = Seg(0, 1000, 1000 + (7 * MB) + 12_345, downloaded: 333);
        var oldEnd = donor.End;

        var piece = SegmentSplitter.TrySplit([donor], 1 * MB, 1)!;

        piece.Start.Should().Be(donor.End + 1, "no gap and no overlap");
        piece.End.Should().Be(oldEnd);
        (SegmentSplitter.Left(donor) + piece.Length!.Value).Should().Be(7 * MB + 12_345 + 1 - 333);
        SegmentSplitter.Left(donor).Should().BeGreaterThanOrEqualTo(piece.Length!.Value, "the donor keeps the larger half");
    }

    [Fact]
    public void Bytes_a_connection_is_writing_are_never_taken()
    {
        var donor = Seg(0, 0, 4 * MB - 1, downloaded: 1 * MB, reserved: 256 * 1024);

        var piece = SegmentSplitter.TrySplit([donor], 512 * 1024, 1)!;

        piece.Start.Should().BeGreaterThanOrEqualTo(donor.Position + donor.Reserved);
        piece.Start.Should().Be((1 * MB) + (256 * 1024) + (((4 * MB) - (1 * MB) - (256 * 1024)) / 2));
    }

    [Fact]
    public void Nothing_is_split_into_pieces_smaller_than_the_minimum()
    {
        SegmentSplitter.TrySplit([Seg(0, 0, (2 * MB) - 2)], 1 * MB, 1).Should().BeNull("2 MB less a byte cannot make two 1 MB pieces");
        SegmentSplitter.TrySplit([Seg(0, 0, (2 * MB) - 1)], 1 * MB, 1).Should().NotBeNull("exactly two 1 MB pieces");
    }

    [Fact]
    public void Finished_open_ended_and_empty_lists_give_nothing()
    {
        SegmentSplitter.TrySplit([], 1, 0).Should().BeNull();
        SegmentSplitter.TrySplit([Seg(0, 0, 10 * MB - 1, downloaded: 10 * MB)], 1, 1).Should().BeNull("finished");
        SegmentSplitter.TrySplit([new Segment { Index = 0, Start = 0, End = Segment.OpenEnded }], 1, 1).Should().BeNull("an unknown end cannot be halved");
    }

    [Fact]
    public void Splitting_again_and_again_always_leaves_the_file_covered_once()
    {
        // As idle connections would: split until nothing is worth splitting, with progress between.
        const long total = (64 * MB) + 777;
        var segments = new List<Segment> { Seg(0, 0, (total / 2) - 1), Seg(1, total / 2, total - 1) };
        var random = new Random(90);
        while (true)
        {
            foreach (var s in segments.Where(s => !s.IsComplete))
            {
                s.Downloaded = Math.Min(s.Length!.Value, s.Downloaded + random.NextInt64(0, 2 * MB));
            }

            var piece = SegmentSplitter.TrySplit(segments, 1 * MB, segments.Max(s => s.Index) + 1);
            if (piece is null)
            {
                break;
            }

            segments.Add(piece);
        }

        var ordered = segments.OrderBy(s => s.Start).ToList();
        ordered[0].Start.Should().Be(0);
        ordered[^1].End.Should().Be(total - 1);
        for (var i = 1; i < ordered.Count; i++)
        {
            ordered[i].Start.Should().Be(ordered[i - 1].End + 1, $"segment {ordered[i].Index} follows segment {ordered[i - 1].Index}");
        }

        segments.Should().OnlyContain(s => s.Downloaded <= s.Length!.Value, "no segment counts bytes past its end");
        segments.Select(s => s.Index).Should().OnlyHaveUniqueItems();
    }
}
