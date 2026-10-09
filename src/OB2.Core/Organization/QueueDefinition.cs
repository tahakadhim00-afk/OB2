using System.Text.Json;
using System.Text.Json.Serialization;

namespace OB2.Core.Organization;

/// <summary>A named queue with a concurrency limit (FR-13) and an optional schedule (FR-16).</summary>
public sealed class QueueDefinition
{
    public const int DefaultConcurrency = 3;

    public long Id { get; set; }

    public required string Name { get; set; }

    public int Concurrency { get; set; } = DefaultConcurrency;

    public QueueSchedule Schedule { get; set; } = new();

    public int SortOrder { get; set; }
}

/// <summary>What to do when a queue finishes (FR-17).</summary>
public enum CompletionAction
{
    None,
    CloseApp,
    Sleep,
    Shutdown,
    RunCommand,
}

/// <summary>
/// Weekday start/stop times (FR-16) and the after-completion action (FR-17). Stored as
/// JSON in <c>queues.schedule_json</c>. A stop time earlier than the start time means the
/// window runs past midnight and belongs to the start day.
/// </summary>
public sealed record QueueSchedule
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public bool Enabled { get; init; }

    public TimeOnly Start { get; init; } = new(1, 0);

    /// <summary>Null: start at <see cref="Start"/> and run until the queue is empty.</summary>
    public TimeOnly? Stop { get; init; }

    public IReadOnlySet<DayOfWeek> Days { get; init; } = AllDays;

    public CompletionAction CompletionAction { get; init; } = CompletionAction.None;

    /// <summary>For <see cref="CompletionAction.RunCommand"/>.</summary>
    public string? Command { get; init; }

    public static IReadOnlySet<DayOfWeek> AllDays { get; } = Enum.GetValues<DayOfWeek>().ToHashSet();

    public string ToJson() => JsonSerializer.Serialize(new ScheduleDto(Enabled, Start.ToString("HH\\:mm", null), Stop?.ToString("HH\\:mm", null), [.. Days.OrderBy(d => d)], CompletionAction, Command), JsonOptions);

    public static QueueSchedule FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new QueueSchedule();
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ScheduleDto>(json, JsonOptions);
            if (dto is null)
            {
                return new QueueSchedule();
            }

            return new QueueSchedule
            {
                Enabled = dto.Enabled,
                Start = TimeOnly.TryParse(dto.Start, null, out var start) ? start : new TimeOnly(1, 0),
                Stop = TimeOnly.TryParse(dto.Stop, null, out var stop) ? stop : null,
                Days = dto.Days is { Count: > 0 } ? dto.Days.ToHashSet() : AllDays,
                CompletionAction = dto.CompletionAction,
                Command = dto.Command,
            };
        }
        catch (JsonException)
        {
            // A damaged schedule disables itself rather than breaking the queue (§4 Reliability).
            return new QueueSchedule();
        }
    }

    private sealed record ScheduleDto(bool Enabled, string? Start, string? Stop, List<DayOfWeek>? Days, CompletionAction CompletionAction, string? Command);
}

/// <summary>Decides whether a schedule's window is open at a local time (FR-16, AC-10).</summary>
public static class ScheduleEvaluator
{
    public static bool IsInWindow(QueueSchedule schedule, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!schedule.Enabled)
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(localNow);
        var today = localNow.DayOfWeek;
        var yesterday = localNow.AddDays(-1).DayOfWeek;

        if (schedule.Stop is not { } stop)
        {
            // Start-only: the window is from the start time to midnight.
            return schedule.Days.Contains(today) && time >= schedule.Start;
        }

        if (stop == schedule.Start)
        {
            return false;
        }

        if (schedule.Start < stop)
        {
            return schedule.Days.Contains(today) && time >= schedule.Start && time < stop;
        }

        // Overnight window, e.g. Mon 23:00 → Tue 06:00, belongs to Monday.
        return (schedule.Days.Contains(today) && time >= schedule.Start)
            || (schedule.Days.Contains(yesterday) && time < stop);
    }
}

public interface IQueueRepository
{
    IReadOnlyList<QueueDefinition> List();

    long Insert(QueueDefinition queue);

    void Update(QueueDefinition queue);

    /// <summary>Downloads in the queue move to <paramref name="moveTo"/>.</summary>
    void Delete(long id, long moveTo);
}
