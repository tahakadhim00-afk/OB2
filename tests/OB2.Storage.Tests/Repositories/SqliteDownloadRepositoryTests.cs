using Microsoft.Data.Sqlite;
using OB2.Core.Downloads;
using OB2.Storage.Database;
using OB2.Storage.Repositories;
using OB2.Tests.Common;

namespace OB2.Storage.Tests.Repositories;

public sealed class SqliteDownloadRepositoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SqliteDownloadRepository _repository;

    public SqliteDownloadRepositoryTests()
    {
        var factory = new SqliteConnectionFactory(_temp.Combine("ob2.db"));
        new MigrationRunner(factory).Run();
        _repository = new SqliteDownloadRepository(factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static DownloadItem NewItem(string name = "a.zip") => new()
    {
        Url = "https://example.com/" + name,
        FileName = name,
        DestPath = @"C:\Downloads\" + name,
        CreatedAt = new DateTimeOffset(2026, 10, 10, 1, 2, 3, TimeSpan.Zero),
    };

    [Fact]
    public void Insert_and_get_round_trip_every_column()
    {
        var item = NewItem();
        item.FinalUrl = "https://cdn.example.com/a.zip";
        item.Status = DownloadStatus.Paused;
        item.TotalBytes = 1000;
        item.DownloadedBytes = 400;
        item.Connections = 8;
        item.SupportsRanges = true;
        item.ETag = "\"e\"";
        item.LastModified = "Wed, 01 Jan 2025 00:00:00 GMT";
        item.ChecksumAlgo = "sha256";
        item.ChecksumValue = new string('A', 64);
        item.Referrer = "https://example.com/";
        item.UserAgent = "UA";
        item.Headers["Accept-Language"] = "ar";
        item.SpeedLimitBps = 100_000;
        item.ErrorCode = "X";
        item.ErrorMessage = "msg";
        item.StartedAt = item.CreatedAt.AddMinutes(1);
        item.CompletedAt = item.CreatedAt.AddMinutes(2);
        item.Source = DownloadSource.Chrome;

        item.Id = _repository.Insert(item);
        var loaded = _repository.Get(item.Id);

        loaded.Should().BeEquivalentTo(item);
    }

    [Fact]
    public void Update_list_and_delete()
    {
        var a = NewItem("a.zip");
        a.Id = _repository.Insert(a);
        var b = NewItem("b.zip");
        b.Id = _repository.Insert(b);

        a.Status = DownloadStatus.Completed;
        _repository.Update(a);
        _repository.Delete(b.Id);

        _repository.List().Should().ContainSingle().Which.Status.Should().Be(DownloadStatus.Completed);
        _repository.Get(b.Id).Should().BeNull();
    }

    [Fact]
    public void Checkpoint_updates_bytes_and_segments_together()
    {
        var item = NewItem();
        item.Id = _repository.Insert(item);
        _repository.SaveSegments(item.Id, [
            new Segment { Index = 0, Start = 0, End = 499 },
            new Segment { Index = 1, Start = 500, End = 999 },
        ]);

        _repository.Checkpoint(item.Id, 700, [
            new Segment { Index = 0, Start = 0, End = 499, Downloaded = 500, Status = SegmentStatus.Done },
            new Segment { Index = 1, Start = 500, End = 999, Downloaded = 200, Status = SegmentStatus.Active, RetryCount = 2 },
        ]);

        _repository.Get(item.Id)!.DownloadedBytes.Should().Be(700);
        var segments = _repository.GetSegments(item.Id);
        segments.Should().HaveCount(2);
        segments[0].Status.Should().Be(SegmentStatus.Done);
        segments[1].Downloaded.Should().Be(200);
        segments[1].RetryCount.Should().Be(2);
    }

    [Fact]
    public void SaveSegments_replaces_previous_plan()
    {
        var item = NewItem();
        item.Id = _repository.Insert(item);
        _repository.SaveSegments(item.Id, [new Segment { Index = 0, Start = 0, End = 9 }, new Segment { Index = 1, Start = 10, End = 19 }]);

        _repository.SaveSegments(item.Id, [new Segment { Index = 0, Start = 0, End = Segment.OpenEnded }]);

        _repository.GetSegments(item.Id).Should().ContainSingle().Which.IsOpenEnded.Should().BeTrue();
    }

    [Fact]
    public void Interrupted_downloads_become_paused_and_active_segments_pending()
    {
        var running = NewItem("r.zip");
        running.Status = DownloadStatus.Downloading;
        running.Id = _repository.Insert(running);
        _repository.SaveSegments(running.Id, [new Segment { Index = 0, Start = 0, End = 9, Status = SegmentStatus.Active }]);
        var done = NewItem("d.zip");
        done.Status = DownloadStatus.Completed;
        done.Id = _repository.Insert(done);

        var recovered = _repository.MarkInterruptedAsPaused();

        recovered.Should().Equal(running.Id);
        _repository.Get(running.Id)!.Status.Should().Be(DownloadStatus.Paused);
        _repository.GetSegments(running.Id)[0].Status.Should().Be(SegmentStatus.Pending);
        _repository.Get(done.Id)!.Status.Should().Be(DownloadStatus.Completed);
    }

    [Fact]
    public void History_events_survive_download_deletion()
    {
        var item = NewItem();
        item.Id = _repository.Insert(item);
        _repository.AddHistoryEvent(item.Id, "added", "manual");

        _repository.GetHistory(item.Id).Should().ContainSingle().Which.EventType.Should().Be("added");
        _repository.Delete(item.Id);
        _repository.GetHistory(item.Id).Should().BeEmpty("the event now has download_id NULL");
    }
}
