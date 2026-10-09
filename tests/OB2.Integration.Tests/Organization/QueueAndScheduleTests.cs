using Microsoft.Data.Sqlite;
using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Organization;
using OB2.Core.Services;
using OB2.Core.Settings;
using OB2.Integration.Tests.Infrastructure;
using OB2.Storage.Database;
using OB2.Storage.Repositories;
using OB2.Tests.Common;

namespace OB2.Integration.Tests.Organization;

/// <summary>Phase 4 core: queues (FR-13/14/15), scheduling (FR-16, AC-10, Flow B), categories (FR-18).</summary>
public sealed class QueueAndScheduleTests : IAsyncLifetime
{
    private const int MB = 1024 * 1024;

    private readonly TempDirectory _temp = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero)); // Monday
    private TestHttpServer _server = null!;
    private SqliteDownloadRepository _downloads = null!;
    private SqliteOrganizationRepository _organization = null!;
    private DownloadEngine _engine = null!;
    private QueueManager _manager = null!;
    private QueueScheduler _scheduler = null!;
    private DownloadService _service = null!;
    private AppSettings _settings = null!;

    private SqliteOrganizationRepository Queues => _organization;

    public async Task InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        var factory = new SqliteConnectionFactory(_temp.Combine("ob2.db"));
        new MigrationRunner(factory).Run();
        _downloads = new SqliteDownloadRepository(factory);
        _organization = new SqliteOrganizationRepository(factory);
        _settings = new AppSettings();
        _settings.Folders.DefaultSaveFolder = _temp.Combine("dl");
        _engine = new DownloadEngine(_downloads, () => EngineFixture.FastOptions with { Connections = 2 });
        _manager = new QueueManager(_engine, _downloads, _organization);
        _scheduler = new QueueScheduler(_manager, _organization, _clock, TimeZoneInfo.Utc);
        _service = new DownloadService(_engine, _downloads, _organization, _organization, _manager, () => _settings);
    }

    public async Task DisposeAsync()
    {
        _manager.Dispose();
        _engine.Dispose();
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public async Task Download_now_starts_immediately_and_lands_in_its_category_folder()
    {
        var item = await _service.AddAsync(new AddDownloadOptions { Url = _server.FileUrl(100_000, "clip.mp4") });

        var done = await _engine.WaitAsync(item.Id);

        done!.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        done.DestPath.Should().Be(Path.Combine(_temp.Combine("dl"), "Video", "clip.mp4"));
        done.CategoryId.Should().Be(_service.Categories.Single(c => c.Name == "Video").Id);
        done.QueueId.Should().Be(_service.DefaultQueueId);
    }

    [Fact]
    public async Task Unknown_extension_goes_to_the_default_folder_without_a_category()
    {
        var item = await _service.AddAsync(new AddDownloadOptions { Url = _server.FileUrl(10, "notes.xyz") });

        (await _engine.WaitAsync(item.Id))!.DestPath.Should().Be(Path.Combine(_temp.Combine("dl"), "notes.xyz"));
        item.CategoryId.Should().BeNull();
    }

    [Fact]
    public async Task Running_queue_respects_its_concurrency_limit_and_order()
    {
        var queue = new QueueDefinition { Name = "Two at a time", Concurrency = 2 };
        Queues.Insert(queue);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var item = await _service.AddAsync(new AddDownloadOptions
            {
                Url = _server.FileUrl(1L * MB, $"q{i}.bin", "bps=2000000"),
                QueueId = queue.Id,
                StartNow = false,
                Connections = 1,
            });
            ids.Add(item.Id);
        }

        // FR-15: move the last item to the front.
        _manager.Move(ids[4], 0);

        var maxActive = 0;
        var firstStarted = new List<long>();
        _engine.StatusChanged += (_, d) =>
        {
            if (d.Status == DownloadStatus.Downloading)
            {
                lock (firstStarted)
                {
                    firstStarted.Add(d.Id);
                }
            }
        };
        var completed = new TaskCompletionSource();
        _manager.QueueCompleted += (_, e) => completed.TrySetResult();

        _manager.StartQueue(queue.Id);
        while (!completed.Task.IsCompleted)
        {
            maxActive = Math.Max(maxActive, ids.Count(_engine.IsActive));
            await Task.WhenAny(completed.Task, Task.Delay(20));
        }

        maxActive.Should().BeLessThanOrEqualTo(2);
        ids.Select(_downloads.Get).Should().OnlyContain(d => d!.Status == DownloadStatus.Completed);
        firstStarted.Take(2).Should().Contain(ids[4], "the reordered item runs in the first batch");
        _manager.IsRunning(queue.Id).Should().BeFalse("a finished queue stops itself");
    }

    [Fact]
    public async Task Queue_completion_reports_the_configured_action()
    {
        var queue = new QueueDefinition { Name = "Then sleep", Schedule = new QueueSchedule { CompletionAction = CompletionAction.Shutdown } };
        Queues.Insert(queue);
        await _service.AddAsync(new AddDownloadOptions { Url = _server.FileUrl(1000, "a.bin"), QueueId = queue.Id, StartNow = false });
        var completed = new TaskCompletionSource<QueueDefinition>();
        _manager.QueueCompleted += (_, e) => completed.TrySetResult(e.Queue);

        _manager.StartQueue(queue.Id);

        (await completed.Task.WaitAsync(TimeSpan.FromSeconds(30))).Schedule.CompletionAction.Should().Be(CompletionAction.Shutdown);
    }

    [Fact]
    public async Task Starting_an_empty_queue_does_not_fire_its_completion_action()
    {
        var queue = new QueueDefinition { Name = "Empty" };
        Queues.Insert(queue);
        var fired = false;
        _manager.QueueCompleted += (_, _) => fired = true;

        _manager.StartQueue(queue.Id);
        await Task.Delay(200);

        fired.Should().BeFalse("shutting the PC down because an empty queue 'finished' would be a nasty surprise");
    }

    [Fact]
    public async Task AC10_and_flow_B_overnight_queue_starts_stops_and_resumes_next_day()
    {
        // Flow B: queue "Night", Start 01:00, Stop 07:00, Mon–Fri. Simulated clock (AC-10).
        var night = new QueueDefinition
        {
            Name = "Night",
            Concurrency = 2,
            Schedule = new QueueSchedule
            {
                Enabled = true,
                Start = new TimeOnly(1, 0),
                Stop = new TimeOnly(7, 0),
                Days = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            },
        };
        Queues.Insert(night);
        const long size = 6L * MB;
        var a = await _service.AddAsync(new AddDownloadOptions { Url = _server.FileUrl(size, "night-a.bin", "bps=1000000"), QueueId = night.Id, StartNow = false, Connections = 2 });
        var b = await _service.AddAsync(new AddDownloadOptions { Url = _server.FileUrl(size, "night-b.bin", "bps=1000000"), QueueId = night.Id, StartNow = false, Connections = 2 });

        // Monday 00:59 – the queue waits.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 12, 0, 59, 0, TimeSpan.Zero);
        await _scheduler.TickAsync();
        _manager.IsRunning(night.Id).Should().BeFalse();
        _engine.IsActive(a.Id).Should().BeFalse();

        // 01:00 – the queue starts both downloads.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);
        await _scheduler.TickAsync();
        _manager.IsRunning(night.Id).Should().BeTrue();
        await WaitUntilAsync(() => _engine.IsActive(a.Id) && _engine.IsActive(b.Id));
        await WaitUntilAsync(() => (_downloads.Get(a.Id)!.DownloadedBytes > 256 * 1024) && (_downloads.Get(b.Id)!.DownloadedBytes > 256 * 1024));

        // 07:00 – the queue stops; progress is saved and the items wait in the queue.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.Zero);
        await _scheduler.TickAsync();
        _manager.IsRunning(night.Id).Should().BeFalse();
        var stoppedA = _downloads.Get(a.Id)!;
        stoppedA.Status.Should().Be(DownloadStatus.Queued);
        stoppedA.DownloadedBytes.Should().BeInRange(1, size - 1);
        _engine.IsActive(a.Id).Should().BeFalse();

        // Tuesday 01:00 – resumes from saved progress and finishes.
        var completed = new TaskCompletionSource();
        _manager.QueueCompleted += (_, _) => completed.TrySetResult();
        var requestsBefore = _server.Requests.Count;
        _clock.UtcNow = new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);
        await _scheduler.TickAsync();
        await completed.Task.WaitAsync(TimeSpan.FromMinutes(1));

        foreach (var id in new[] { a.Id, b.Id })
        {
            var done = _downloads.Get(id)!;
            done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
            EngineFixture.Sha256OfFile(done.DestPath).Should().Be(TestHttpServer.Sha256Of(size));
        }

        _server.Requests.Skip(requestsBefore).Should().Contain(
            r => r.Method == "GET" && r.Header("Range") != null && !r.Header("Range")!.StartsWith("bytes=0-", StringComparison.Ordinal),
            "Tuesday's run continues Monday's bytes");
    }

    [Fact]
    public async Task Saturday_is_skipped_by_a_weekday_schedule()
    {
        var queue = new QueueDefinition
        {
            Name = "Weekdays",
            Schedule = new QueueSchedule { Enabled = true, Start = new TimeOnly(1, 0), Stop = new TimeOnly(7, 0), Days = new HashSet<DayOfWeek> { DayOfWeek.Monday } },
        };
        Queues.Insert(queue);

        _clock.UtcNow = new DateTimeOffset(2026, 10, 17, 1, 30, 0, TimeSpan.Zero); // Saturday
        await _scheduler.TickAsync();

        _manager.IsRunning(queue.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Opening_the_app_inside_a_window_starts_the_queue()
    {
        var queue = new QueueDefinition { Name = "Late", Schedule = new QueueSchedule { Enabled = true, Start = new TimeOnly(1, 0), Stop = new TimeOnly(7, 0) } };
        Queues.Insert(queue);

        _clock.UtcNow = new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);
        await _scheduler.TickAsync();

        _manager.IsRunning(queue.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Stop_and_resume_all_cover_every_download()
    {
        // UC-4: pause everything before a video call, resume afterwards.
        var a = await _service.AddAsync(new AddDownloadOptions { Url = _server.FileUrl(4L * MB, "a.bin", "bps=500000") });
        var b = await _service.AddAsync(new AddDownloadOptions { Url = _server.FileUrl(4L * MB, "b.bin", "bps=500000") });
        await WaitUntilAsync(() => _engine.IsActive(a.Id) && _engine.IsActive(b.Id));

        await _service.StopAllAsync();

        _engine.ActiveIds.Should().BeEmpty();
        _downloads.Get(a.Id)!.Status.Should().Be(DownloadStatus.Paused);

        _service.StartAll();

        _engine.IsActive(a.Id).Should().BeTrue();
        _engine.IsActive(b.Id).Should().BeTrue();
        await _service.StopAllAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(20);
        }
    }
}
