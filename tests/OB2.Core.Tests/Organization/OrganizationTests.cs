using OB2.Core.Organization;

namespace OB2.Core.Tests.Organization;

public sealed class ScheduleEvaluatorTests
{
    // 2026-10-12 is a Monday.
    private static DateTime Mon(int h, int m = 0) => new(2026, 10, 12, h, m, 0);

    private static readonly QueueSchedule Night = new()
    {
        Enabled = true,
        Start = new TimeOnly(1, 0),
        Stop = new TimeOnly(7, 0),
        Days = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
    };

    [Theory]
    [InlineData(0, 59, false)]
    [InlineData(1, 0, true)]
    [InlineData(3, 30, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(23, 0, false)]
    public void Same_day_window_mon_to_fri_01_to_07(int h, int m, bool expected)
    {
        ScheduleEvaluator.IsInWindow(Night, Mon(h, m)).Should().Be(expected);
    }

    [Fact]
    public void Weekend_days_are_excluded()
    {
        var saturday = new DateTime(2026, 10, 17, 3, 0, 0);

        ScheduleEvaluator.IsInWindow(Night, saturday).Should().BeFalse();
    }

    [Fact]
    public void Disabled_schedule_is_never_in_window()
    {
        ScheduleEvaluator.IsInWindow(Night with { Enabled = false }, Mon(3)).Should().BeFalse();
    }

    [Fact]
    public void Overnight_window_belongs_to_its_start_day()
    {
        // Friday 23:00 → Saturday 06:00, Fridays only.
        var schedule = new QueueSchedule
        {
            Enabled = true,
            Start = new TimeOnly(23, 0),
            Stop = new TimeOnly(6, 0),
            Days = new HashSet<DayOfWeek> { DayOfWeek.Friday },
        };
        var friday = new DateTime(2026, 10, 16);

        ScheduleEvaluator.IsInWindow(schedule, friday.AddHours(22)).Should().BeFalse();
        ScheduleEvaluator.IsInWindow(schedule, friday.AddHours(23)).Should().BeTrue();
        ScheduleEvaluator.IsInWindow(schedule, friday.AddHours(29)).Should().BeTrue("Saturday 05:00 is still Friday's window");
        ScheduleEvaluator.IsInWindow(schedule, friday.AddHours(30)).Should().BeFalse();
        ScheduleEvaluator.IsInWindow(schedule, friday.AddHours(5)).Should().BeFalse("Friday 05:00 belongs to Thursday, which is not selected");
    }

    [Fact]
    public void Start_only_schedule_runs_until_midnight()
    {
        var schedule = new QueueSchedule { Enabled = true, Start = new TimeOnly(20, 0), Stop = null };

        ScheduleEvaluator.IsInWindow(schedule, Mon(19, 59)).Should().BeFalse();
        ScheduleEvaluator.IsInWindow(schedule, Mon(23, 59)).Should().BeTrue();
    }

    [Fact]
    public void Schedule_round_trips_through_json()
    {
        var schedule = Night with { CompletionAction = CompletionAction.Shutdown, Command = "echo done" };

        var restored = QueueSchedule.FromJson(schedule.ToJson());

        restored.Enabled.Should().BeTrue();
        restored.Start.Should().Be(new TimeOnly(1, 0));
        restored.Stop.Should().Be(new TimeOnly(7, 0));
        restored.Days.Should().BeEquivalentTo(Night.Days);
        restored.CompletionAction.Should().Be(CompletionAction.Shutdown);
        restored.Command.Should().Be("echo done");
        schedule.ToJson().Should().Contain("\"start\":\"01:00\"").And.Contain("\"Monday\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void Damaged_schedule_json_disables_the_schedule(string? json)
    {
        QueueSchedule.FromJson(json).Enabled.Should().BeFalse();
    }
}

public sealed class CategoryResolverTests
{
    private static readonly Category[] Categories =
    [
        new() { Id = 1, Name = "Documents", Extensions = ["pdf", "docx"] },
        new() { Id = 2, Name = "Video", Extensions = ["mp4", "mkv"] },
        new() { Id = 3, Name = "Compressed", Extensions = ["zip", "gz", "tar.gz"] },
        new() { Id = 4, Name = "Archive", Extensions = ["tar"] },
    ];

    [Theory]
    [InlineData("report.PDF", "Documents")]
    [InlineData("movie.mkv", "Video")]
    [InlineData("backup.tar.gz", "Compressed")]
    [InlineData("data.gz", "Compressed")]
    [InlineData("old.tar", "Archive")]
    [InlineData("README", null)]
    [InlineData("setup.exe", null)]
    public void Matches_by_extension(string fileName, string? expected)
    {
        CategoryResolver.Match(Categories, fileName)?.Name.Should().Be(expected);
        if (expected is null)
        {
            CategoryResolver.Match(Categories, fileName).Should().BeNull();
        }
    }

    [Fact]
    public void Extensions_are_normalized()
    {
        Category.ParseExtensions(" .MP3, flac;; wav  .mp3").Should().Equal("mp3", "flac", "wav");
    }

    [Fact]
    public void Folder_defaults_to_a_subfolder_named_after_the_category()
    {
        new Category { Name = "Video" }.ResolveSaveFolder(@"D:\Downloads").Should().Be(@"D:\Downloads\Video");
        new Category { Name = "Video", SaveFolder = @"E:\Movies" }.ResolveSaveFolder(@"D:\Downloads").Should().Be(@"E:\Movies");
    }
}
