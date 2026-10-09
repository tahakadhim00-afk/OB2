using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OB2.Core.Downloads;
using OB2.Core.Engine;

namespace OB2.Core.Organization;

/// <summary>Raised when a running queue has started at least one download and has none left.</summary>
public sealed class QueueCompletedEventArgs(QueueDefinition queue) : EventArgs
{
    public QueueDefinition Queue { get; } = queue;
}

/// <summary>
/// Runs queues (FR-13, FR-14): while a queue is running it keeps up to its concurrency
/// limit of <c>queued</c> items downloading, in queue order. "Download now" bypasses
/// queues by calling the engine directly.
/// </summary>
public sealed partial class QueueManager : IDisposable
{
    private readonly DownloadEngine _engine;
    private readonly IDownloadRepository _downloads;
    private readonly IQueueRepository _queues;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    // Queue id → number of downloads this run has started (0 = running but nothing started yet).
    private readonly Dictionary<long, int> _running = [];
    private int _pumping;
    private int _pumpRequested;

    public QueueManager(DownloadEngine engine, IDownloadRepository downloads, IQueueRepository queues, ILogger<QueueManager>? logger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _logger = logger ?? NullLogger<QueueManager>.Instance;
        _engine.JobEnded += OnJobEnded;
        _engine.StatusChanged += OnStatusChanged;
    }

    public event EventHandler<QueueCompletedEventArgs>? QueueCompleted;

    /// <summary>A queue started or stopped.</summary>
    public event EventHandler? StateChanged;

    public bool IsRunning(long queueId)
    {
        lock (_lock)
        {
            return _running.ContainsKey(queueId);
        }
    }

    public IReadOnlyCollection<long> RunningQueues
    {
        get
        {
            lock (_lock)
            {
                return [.. _running.Keys];
            }
        }
    }

    public void StartQueue(long queueId)
    {
        lock (_lock)
        {
            if (!_running.TryAdd(queueId, 0))
            {
                return;
            }
        }

        LogQueueStarted(queueId);
        StateChanged?.Invoke(this, EventArgs.Empty);
        Pump();
    }

    /// <summary>
    /// Stops a queue: its running downloads are checkpointed and go back to <c>queued</c>,
    /// so the next start continues them from saved progress (Flow B).
    /// </summary>
    public async Task StopQueueAsync(long queueId)
    {
        lock (_lock)
        {
            if (!_running.Remove(queueId))
            {
                return;
            }
        }

        var active = _downloads.List().Where(d => d.QueueId == queueId && _engine.IsActive(d.Id)).Select(d => d.Id).ToList();
        await Task.WhenAll(active.Select(_engine.RequeueAsync)).ConfigureAwait(false);
        LogQueueStopped(queueId, active.Count);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves a download to <paramref name="position"/> within its queue (FR-15).</summary>
    public void Move(long downloadId, int position)
    {
        var item = _downloads.Get(downloadId);
        if (item is null)
        {
            return;
        }

        var siblings = _downloads.List()
            .Where(d => d.QueueId == item.QueueId && d.Id != downloadId)
            .OrderBy(d => d.QueuePosition).ThenBy(d => d.Id)
            .ToList();
        siblings.Insert(Math.Clamp(position, 0, siblings.Count), item);
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].QueuePosition != i)
            {
                siblings[i].QueuePosition = i;
                _downloads.Update(siblings[i]);
            }
        }
    }

    /// <summary>Position for a download appended to the end of a queue.</summary>
    public long NextPosition(long? queueId) =>
        _downloads.List().Where(d => d.QueueId == queueId).Select(d => d.QueuePosition + 1).DefaultIfEmpty(0).Max();

    /// <summary>Fills free slots in every running queue. Safe to call from any thread.</summary>
    public void Pump()
    {
        Interlocked.Exchange(ref _pumpRequested, 1);
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
        {
            return; // the running pump will see the request flag and go again
        }

        try
        {
            while (Interlocked.Exchange(ref _pumpRequested, 0) == 1)
            {
                PumpOnce();
            }
        }
        finally
        {
            Volatile.Write(ref _pumping, 0);
        }
    }

    public void Dispose()
    {
        _engine.JobEnded -= OnJobEnded;
        _engine.StatusChanged -= OnStatusChanged;
    }

    private void PumpOnce()
    {
        long[] running;
        lock (_lock)
        {
            running = [.. _running.Keys];
        }

        if (running.Length == 0)
        {
            return;
        }

        var queues = _queues.List().ToDictionary(q => q.Id);
        var downloads = _downloads.List();
        foreach (var queueId in running)
        {
            if (!queues.TryGetValue(queueId, out var queue))
            {
                lock (_lock)
                {
                    _running.Remove(queueId);
                }

                continue;
            }

            var members = downloads.Where(d => d.QueueId == queueId).ToList();
            var active = members.Count(d => _engine.IsActive(d.Id));
            var waiting = members
                .Where(d => d.Status == DownloadStatus.Queued && !_engine.IsActive(d.Id))
                .OrderBy(d => d.QueuePosition).ThenBy(d => d.Id)
                .ToList();

            foreach (var next in waiting.Take(Math.Max(0, queue.Concurrency - active)))
            {
                if (_engine.Start(next.Id))
                {
                    active++;
                    lock (_lock)
                    {
                        if (_running.TryGetValue(queueId, out var started))
                        {
                            _running[queueId] = started + 1;
                        }
                    }
                }
            }

            if (active == 0 && waiting.Count == 0)
            {
                CompleteIfStarted(queue);
            }
        }
    }

    private void CompleteIfStarted(QueueDefinition queue)
    {
        lock (_lock)
        {
            if (!_running.TryGetValue(queue.Id, out var started) || started == 0)
            {
                return;
            }

            _running.Remove(queue.Id);
        }

        LogQueueCompleted(queue.Id, queue.Name);
        StateChanged?.Invoke(this, EventArgs.Empty);
        QueueCompleted?.Invoke(this, new QueueCompletedEventArgs(queue));
    }

    // A finished job frees a slot.
    private void OnJobEnded(object? sender, long id) => Pump();

    // A newly queued item (added, or requeued by the user) may fit in a free slot.
    private void OnStatusChanged(object? sender, DownloadItem item)
    {
        if (item.Status == DownloadStatus.Queued && item.QueueId is { } queueId && IsRunning(queueId))
        {
            _ = Task.Run(Pump);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queue {QueueId} started")]
    private partial void LogQueueStarted(long queueId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queue {QueueId} stopped; {Count} running download(s) requeued")]
    private partial void LogQueueStopped(long queueId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queue {QueueId} ({Name}) finished")]
    private partial void LogQueueCompleted(long queueId, string name);
}
