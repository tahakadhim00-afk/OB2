using Microsoft.Data.Sqlite;
using OB2.Core.Downloads;
using OB2.Core.Organization;
using OB2.Storage.Database;
using OB2.Storage.Repositories;
using OB2.Tests.Common;

namespace OB2.Storage.Tests.Repositories;

public sealed class SqliteOrganizationRepositoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ICategoryRepository _categories;
    private readonly IQueueRepository _queues;
    private readonly SqliteDownloadRepository _downloads;

    public SqliteOrganizationRepositoryTests()
    {
        var factory = new SqliteConnectionFactory(_temp.Combine("ob2.db"));
        new MigrationRunner(factory).Run();
        var repository = new SqliteOrganizationRepository(factory);
        _categories = repository;
        _queues = repository;
        _downloads = new SqliteDownloadRepository(factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void Migration_seeds_default_categories_and_main_queue()
    {
        _categories.List().Select(c => c.Name).Should().Equal("Documents", "Music", "Video", "Programs", "Compressed");
        _categories.List().Single(c => c.Name == "Video").Extensions.Should().Contain(["mp4", "mkv"]);
        _queues.List().Should().ContainSingle().Which.Should().Match<QueueDefinition>(q => q.Name == "Main" && q.Concurrency == 3);
    }

    [Fact]
    public void Custom_category_round_trips_and_delete_detaches_downloads()
    {
        var custom = new Category { Name = "Datasets", SaveFolder = @"D:\Data", Extensions = ["csv", "parquet"] };
        _categories.Insert(custom);
        var item = NewDownload();
        item.CategoryId = custom.Id;
        item.Id = _downloads.Insert(item);

        custom.Extensions.Add("arrow");
        _categories.Update(custom);
        _categories.List().Single(c => c.Id == custom.Id).Extensions.Should().Equal("csv", "parquet", "arrow");

        _categories.Delete(custom.Id);
        _categories.List().Should().NotContain(c => c.Id == custom.Id);
        _downloads.Get(item.Id)!.CategoryId.Should().BeNull();
    }

    [Fact]
    public void Queue_with_schedule_round_trips_and_delete_moves_downloads()
    {
        var main = _queues.List().Single();
        var night = new QueueDefinition
        {
            Name = "Night",
            Concurrency = 2,
            SortOrder = 1,
            Schedule = new QueueSchedule { Enabled = true, Start = new TimeOnly(1, 0), Stop = new TimeOnly(7, 0), CompletionAction = CompletionAction.Sleep },
        };
        _queues.Insert(night);
        var item = NewDownload();
        item.QueueId = night.Id;
        item.Id = _downloads.Insert(item);

        var loaded = _queues.List().Single(q => q.Name == "Night");
        loaded.Concurrency.Should().Be(2);
        loaded.Schedule.Stop.Should().Be(new TimeOnly(7, 0));
        loaded.Schedule.CompletionAction.Should().Be(CompletionAction.Sleep);

        _queues.Delete(night.Id, moveTo: main.Id);
        _downloads.Get(item.Id)!.QueueId.Should().Be(main.Id);
    }

    [Fact]
    public void Queue_position_is_stored()
    {
        var item = NewDownload();
        item.QueuePosition = 7;
        item.Id = _downloads.Insert(item);

        _downloads.Get(item.Id)!.QueuePosition.Should().Be(7);
    }

    private static DownloadItem NewDownload() => new()
    {
        Url = "https://example.com/a.csv",
        FileName = "a.csv",
        DestPath = @"C:\Downloads\a.csv",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
