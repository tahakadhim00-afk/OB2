using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Http;
using OB2.Core.Integrity;
using OB2.Core.Organization;
using OB2.Core.Settings;

namespace OB2.Core.Services;

/// <summary>What the Add Download dialog (§9.2) or a capture hands over.</summary>
public sealed record AddDownloadOptions
{
    public required Uri Url { get; init; }

    public string? FileName { get; init; }

    public string? FileNameHint { get; init; }

    /// <summary>Null resolves from the category, then the default folder.</summary>
    public string? SaveFolder { get; init; }

    public long? CategoryId { get; init; }

    public long? QueueId { get; init; }

    public int? Connections { get; init; }

    public Checksum? Checksum { get; init; }

    public long? SpeedLimitBps { get; init; }

    /// <summary>"Download Now" (true) or "Download Later" (false).</summary>
    public bool StartNow { get; init; } = true;

    public string? Referrer { get; init; }

    public string? UserAgent { get; init; }

    public string? Cookies { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public DownloadSource Source { get; init; } = DownloadSource.Manual;
}

/// <summary>
/// Application-level download operations shared by the window, tray, CLI hand-off, and
/// browser capture. UI-free so it can be tested without WPF.
/// </summary>
public sealed class DownloadService
{
    private readonly DownloadEngine _engine;
    private readonly IDownloadRepository _downloads;
    private readonly ICategoryRepository _categories;
    private readonly IQueueRepository _queues;
    private readonly QueueManager _queueManager;
    private readonly Func<AppSettings> _settings;

    public DownloadService(
        DownloadEngine engine,
        IDownloadRepository downloads,
        ICategoryRepository categories,
        IQueueRepository queues,
        QueueManager queueManager,
        Func<AppSettings> settings)
    {
        _engine = engine;
        _downloads = downloads;
        _categories = categories;
        _queues = queues;
        _queueManager = queueManager;
        _settings = settings;
        _engine.StatusChanged += OnStatusChanged;
    }

    /// <summary>Raised on a background thread when a download completes.</summary>
    public event EventHandler<DownloadItem>? Completed;

    /// <summary>Raised on a background thread when a download fails.</summary>
    public event EventHandler<DownloadItem>? Failed;

    public DownloadEngine Engine => _engine;

    public QueueManager Queues => _queueManager;

    public IReadOnlyList<Category> Categories => _categories.List();

    public IReadOnlyList<QueueDefinition> QueueList => _queues.List();

    public IReadOnlyList<DownloadItem> List() => _downloads.List();

    public DownloadItem? Get(long id) => _downloads.Get(id);

    /// <summary>Saved segment progress, for the details panel of a download that is not running.</summary>
    public IReadOnlyList<Segment> SegmentsOf(long id) => _downloads.GetSegments(id);

    public string DefaultSaveFolder =>
        _settings().Folders.DefaultSaveFolder is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public long DefaultQueueId => _queues.List().OrderBy(q => q.SortOrder).ThenBy(q => q.Id).First().Id;

    public Category? CategoryFor(string? fileName) => CategoryResolver.Match(_categories.List(), fileName);

    public string SaveFolderFor(long? categoryId)
    {
        var category = categoryId is { } id ? _categories.List().FirstOrDefault(c => c.Id == id) : null;
        return category?.ResolveSaveFolder(DefaultSaveFolder) ?? DefaultSaveFolder;
    }

    public Task<ProbeResult> ProbeAsync(AddDownloadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _engine.ProbeAsync(ToRequest(options, DefaultSaveFolder), cancellationToken);
    }

    /// <summary>Adds a download, choosing category and folder when not given, and starts it or queues it.</summary>
    public async Task<DownloadItem> AddAsync(AddDownloadOptions options, ProbeResult? probe = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        probe ??= await ProbeAsync(options, cancellationToken).ConfigureAwait(false);

        var name = options.FileName ?? probe.SuggestedFileName;
        var categoryId = options.CategoryId ?? CategoryFor(name)?.Id;
        var folder = options.SaveFolder ?? SaveFolderFor(categoryId);
        var queueId = options.QueueId ?? DefaultQueueId;

        var request = ToRequest(options with { CategoryId = categoryId, QueueId = queueId }, folder) with
        {
            QueuePosition = _queueManager.NextPosition(queueId),
        };
        var item = await _engine.AddAsync(request, probe, cancellationToken).ConfigureAwait(false);

        if (options.StartNow)
        {
            _engine.Start(item.Id);
        }
        else if (_queueManager.IsRunning(queueId))
        {
            _queueManager.Pump();
        }

        return item;
    }

    /// <summary>Resume / Retry (FR-14): starts paused, failed, or queued downloads now.</summary>
    public void Start(IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            _engine.Start(id);
        }
    }

    public Task StopAsync(IEnumerable<long> ids) => Task.WhenAll(ids.Select(_engine.PauseAsync));

    public Task StopAllAsync() => _engine.PauseAllAsync();

    /// <summary>Resume All: every paused or failed download (UC-4).</summary>
    public void StartAll() => Start(_downloads.List().Where(d => d.Status is DownloadStatus.Paused or DownloadStatus.Failed).Select(d => d.Id));

    public Task DeleteAsync(IEnumerable<long> ids, bool deleteFiles) => Task.WhenAll(ids.Select(id => _engine.DeleteAsync(id, deleteFiles)));

    /// <summary>Moves a download to another queue (end of its order) or changes its category.</summary>
    public void Assign(long id, long? queueId, long? categoryId)
    {
        var item = _downloads.Get(id);
        if (item is null)
        {
            return;
        }

        if (queueId is { } q && q != item.QueueId)
        {
            item.QueueId = q;
            item.QueuePosition = _queueManager.NextPosition(q);
        }

        item.CategoryId = categoryId ?? item.CategoryId;
        _engine.Update(item);
    }

    /// <summary>"Download Later" for an existing paused item: put it back in its queue.</summary>
    public void Enqueue(IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            var item = _downloads.Get(id);
            if (item is { Status: DownloadStatus.Paused or DownloadStatus.Failed } && !_engine.IsActive(id))
            {
                item.Status = DownloadStatus.Queued;
                _engine.Update(item);
            }
        }
    }

    private static DownloadRequest ToRequest(AddDownloadOptions o, string folder) => new()
    {
        Url = o.Url,
        SaveFolder = folder,
        FileName = o.FileName,
        FileNameHint = o.FileNameHint,
        Referrer = o.Referrer,
        UserAgent = o.UserAgent,
        Cookies = o.Cookies,
        Headers = o.Headers,
        Connections = o.Connections,
        Checksum = o.Checksum,
        Source = o.Source,
        QueueId = o.QueueId,
        CategoryId = o.CategoryId,
        SpeedLimitBps = o.SpeedLimitBps,
    };

    private void OnStatusChanged(object? sender, DownloadItem item)
    {
        switch (item.Status)
        {
            case DownloadStatus.Completed:
                Completed?.Invoke(this, item);
                break;
            case DownloadStatus.Failed:
                Failed?.Invoke(this, item);
                break;
        }
    }
}
