using OB2.Core.Time;

namespace OB2.Core.Organization;

/// <summary>
/// Starts and stops queues at their scheduled times (FR-16). Works on edges: entering a
/// window starts the queue, leaving it stops the queue. The app calls <see cref="Tick"/> on
/// a timer; tests call it with a simulated clock (AC-10).
/// </summary>
public sealed class QueueScheduler
{
    private readonly QueueManager _manager;
    private readonly IQueueRepository _queues;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly Dictionary<long, bool> _inWindow = [];
    private readonly object _lock = new();

    public QueueScheduler(QueueManager manager, IQueueRepository queues, IClock clock, TimeZoneInfo? timeZone = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public async Task TickAsync()
    {
        var local = TimeZoneInfo.ConvertTime(_clock.UtcNow, _timeZone).DateTime;
        var toStart = new List<long>();
        var toStop = new List<long>();

        lock (_lock)
        {
            foreach (var queue in _queues.List())
            {
                var now = ScheduleEvaluator.IsInWindow(queue.Schedule, local);
                var before = _inWindow.GetValueOrDefault(queue.Id);
                if (now && !before)
                {
                    toStart.Add(queue.Id);
                }
                else if (!now && before && queue.Schedule.Stop is not null)
                {
                    toStop.Add(queue.Id);
                }

                _inWindow[queue.Id] = now;
            }
        }

        foreach (var id in toStop)
        {
            await _manager.StopQueueAsync(id).ConfigureAwait(false);
        }

        foreach (var id in toStart)
        {
            _manager.StartQueue(id);
        }
    }

    /// <summary>Synchronous wrapper for timers.</summary>
    public void Tick() => TickAsync().GetAwaiter().GetResult();
}
