using OB2.Core.Downloads;
using OB2.Core.Files;
using OB2.Tests.Common;

namespace OB2.Core.Tests.Files;

public sealed class OrphanScannerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Part_file_with_no_row_is_an_orphan()
    {
        var path = WritePart("leftover.zip", 1234);

        var orphans = new OrphanScanner(new StubRepository()).Scan([_temp.Path]);

        var orphan = orphans.Should().ContainSingle().Subject;
        orphan.PartPath.Should().Be(path);
        orphan.SizeBytes.Should().Be(1234);
        orphan.FileName.Should().Be("leftover.zip", "the suffix is dropped for display");
    }

    [Fact]
    public void Part_file_of_a_paused_download_is_not_an_orphan()
    {
        // The row still owns this file; reporting it would invite the user to delete live progress.
        var path = WritePart("paused.zip", 10);
        var repository = new StubRepository(new DownloadItem
        {
            Url = "https://example.com/paused.zip",
            FileName = "paused.zip",
            DestPath = path[..^DownloadItem.PartSuffix.Length],
            Status = DownloadStatus.Paused,
        });

        new OrphanScanner(repository).Scan([_temp.Path]).Should().BeEmpty();
    }

    [Fact]
    public void Finished_files_and_other_extensions_are_ignored()
    {
        File.WriteAllText(_temp.Combine("done.zip"), "x");
        File.WriteAllText(_temp.Combine("notes.txt"), "x");

        new OrphanScanner(new StubRepository()).Scan([_temp.Path]).Should().BeEmpty();
    }

    [Fact]
    public void Missing_and_duplicate_folders_do_not_fail_the_scan()
    {
        WritePart("one.bin", 5);

        var orphans = new OrphanScanner(new StubRepository())
            .Scan([_temp.Path, Path.Combine(_temp.Path, "nope"), _temp.Path, "", "  "]);

        orphans.Should().ContainSingle("the same folder listed twice reports the file once");
    }

    [Fact]
    public void Delete_removes_an_orphan_but_refuses_anything_else()
    {
        var part = WritePart("gone.zip", 1);
        var finished = _temp.Combine("keep.zip");
        File.WriteAllText(finished, "x");

        OrphanScanner.Delete(part);
        File.Exists(part).Should().BeFalse();

        var refuse = () => OrphanScanner.Delete(finished);
        refuse.Should().Throw<ArgumentException>("only .pgpart files may be deleted this way");
        File.Exists(finished).Should().BeTrue();
    }

    private string WritePart(string name, int bytes)
    {
        var path = _temp.Combine(name + DownloadItem.PartSuffix);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>Only <see cref="List"/> is reached by the scanner; the rest is never called.</summary>
    private sealed class StubRepository(params DownloadItem[] items) : IDownloadRepository
    {
        public IReadOnlyList<DownloadItem> List() => items;

        public long Insert(DownloadItem item) => throw new NotSupportedException();

        public void Update(DownloadItem item) => throw new NotSupportedException();

        public DownloadItem? Get(long id) => throw new NotSupportedException();

        public void Delete(long id) => throw new NotSupportedException();

        public IReadOnlyList<Segment> GetSegments(long downloadId) => throw new NotSupportedException();

        public void SaveSegments(long downloadId, IReadOnlyList<Segment> segments) => throw new NotSupportedException();

        public void Checkpoint(long downloadId, long downloadedBytes, IReadOnlyList<Segment> segments) =>
            throw new NotSupportedException();

        public void AddHistoryEvent(long? downloadId, string eventType, string? detail = null) =>
            throw new NotSupportedException();

        public IReadOnlyList<HistoryEvent> GetHistory(long downloadId) => throw new NotSupportedException();

        public int PruneHistory(int retentionDays) => throw new NotSupportedException();

        public IReadOnlyList<long> MarkInterruptedAsPaused() => throw new NotSupportedException();
    }
}
