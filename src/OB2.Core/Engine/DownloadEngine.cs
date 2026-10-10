using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OB2.Core.Downloads;
using OB2.Core.Files;
using OB2.Core.Http;
using OB2.Core.Media;
using OB2.Core.Time;

namespace OB2.Core.Engine;

/// <summary>
/// Owns the lifecycle of all downloads: add, start, pause, delete, and live progress. Has no
/// UI dependency (§7.2); the UI and the CLI both drive it.
/// </summary>
public sealed partial class DownloadEngine : IDisposable
{
    private readonly IDownloadRepository _repository;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<EngineOptions> _options;
    private readonly IClock _clock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, IDownloadJob> _jobs = new();
    private readonly HostConnectionLimiter _hostLimiter;

    // Cookies live in memory for the life of the process (§11.1); with "remember auth" on they
    // are also kept encrypted in the cookie store until the download finishes (D-67).
    private readonly ConcurrentDictionary<long, string> _cookies = new();
    private readonly ICookieStore? _cookieStore;
    private readonly MediaTools? _mediaTools;
    private readonly object _startLock = new();

    public DownloadEngine(
        IDownloadRepository repository,
        Func<EngineOptions> options,
        HttpClient? http = null,
        IClock? clock = null,
        ILoggerFactory? loggerFactory = null,
        ICookieStore? cookieStore = null,
        MediaTools? mediaTools = null)
    {
        _cookieStore = cookieStore;
        _mediaTools = mediaTools;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsHttp = http is null;
        _http = http ?? DownloadHttpClient.Create(options().ConnectTimeout);
        _clock = clock ?? SystemClock.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<DownloadEngine>();
        _hostLimiter = new HostConnectionLimiter(options().MaxConnectionsPerHost);
    }

    /// <summary>§16.1 / D-66: an expired download address is refreshed at most this many times per run.</summary>
    public const int MaxUrlRefreshes = 3;

    /// <summary>Global speed limit shared by every download (FR-9). Null rate is unlimited.</summary>
    public TokenBucket GlobalLimiter { get; } = new();

    /// <summary>Raised on a background thread whenever a download changes status.</summary>
    public event EventHandler<DownloadItem>? StatusChanged;

    /// <summary>Raised after a job has finished and no longer counts as active.</summary>
    public event EventHandler<long>? JobEnded;

    public IReadOnlyCollection<long> ActiveIds => [.. _jobs.Where(j => !j.Value.Completion.IsCompleted).Select(j => j.Key)];

    public bool IsActive(long id) => _jobs.TryGetValue(id, out var job) && !job.Completion.IsCompleted;

    public async Task<ProbeResult> ProbeAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = _options();
        var context = new RequestContext(request.UserAgent ?? options.UserAgent, request.Referrer, request.Cookies, request.Headers);
        return await new UrlProber(_http, options.ConnectTimeout + options.ReadIdleTimeout)
            .ProbeAsync(request.Url, context, request.FileName ?? request.FileNameHint, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Creates the download record. Probes first unless <paramref name="probe"/> is supplied.</summary>
    public async Task<DownloadItem> AddAsync(DownloadRequest request, ProbeResult? probe = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        UrlValidator.EnsureDownloadable(request.Url);
        probe ??= await ProbeAsync(request, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(request.SaveFolder);
        var name = string.IsNullOrWhiteSpace(request.FileName) ? probe.SuggestedFileName : request.FileName;
        var preferred = SafePath.Combine(request.SaveFolder, name);

        DownloadItem item;
        lock (_startLock)
        {
            var taken = _repository.List()
                .Where(d => d.Status != DownloadStatus.Completed)
                .Select(d => d.DestPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var destination = UniquePath.Allocate(preferred, taken.Contains);

            item = new DownloadItem
            {
                Url = request.Url.AbsoluteUri,
                FinalUrl = probe.FinalUrl.AbsoluteUri,
                FileName = Path.GetFileName(destination),
                DestPath = destination,
                CategoryId = request.CategoryId,
                QueueId = request.QueueId,
                QueuePosition = request.QueuePosition,
                Status = DownloadStatus.Queued,
                TotalBytes = probe.TotalBytes,
                Connections = Math.Clamp(request.Connections ?? _options().Connections, 1, 32),
                SupportsRanges = probe.SupportsRanges,
                ETag = probe.ETag,
                LastModified = probe.LastModified,
                ChecksumAlgo = request.Checksum?.AlgorithmName,
                ChecksumValue = request.Checksum?.HexValue,
                Referrer = request.Referrer,
                UserAgent = request.UserAgent,
                Headers = DownloadHttpClient.PersistableHeaders(request.Headers),
                SpeedLimitBps = request.SpeedLimitBps,
                CreatedAt = _clock.UtcNow,
                Source = request.Source,
                MediaFormat = request.MediaFormat,
                MediaContainer = request.MediaContainer,
            };
            item.Id = _repository.Insert(item);
        }

        if (!string.IsNullOrEmpty(request.Cookies))
        {
            _cookies[item.Id] = request.Cookies;
            if (_options().RememberAuth)
            {
                TrySaveCookies(item.Id, request.Cookies);
            }
        }

        _repository.AddHistoryEvent(item.Id, "added", item.Source.ToDbString());
        LogAdded(item.Id, item.FileName);
        StatusChanged?.Invoke(this, item.Clone());
        return item;
    }

    /// <summary>Starts (or resumes) a download in the background. False if it is already running or finished.</summary>
    public bool Start(long id)
    {
        lock (_startLock)
        {
            if (_jobs.TryGetValue(id, out var existing))
            {
                if (!existing.Completion.IsCompleted)
                {
                    return false;
                }

                // Finished but its cleanup continuation has not run yet: it no longer owns the download.
                _jobs.TryRemove(new KeyValuePair<long, IDownloadJob>(id, existing));
            }

            var item = _repository.Get(id);
            if (item is null || item.Status == DownloadStatus.Completed)
            {
                return false;
            }

            if (!_cookies.TryGetValue(id, out var cookies) && TryLoadCookies(id) is { } saved)
            {
                // After a restart: the cookies the browser gave, kept encrypted (D-67).
                cookies = _cookies[id] = saved;
            }

            // A video page goes through yt-dlp (D-71); everything else through the segment engine.
            IDownloadJob job = item.IsMedia
                ? new MediaJob(id, _repository, _mediaTools, _clock, _loggerFactory.CreateLogger<MediaJob>(), OnJobStatusChanged)
                : new DownloadJob(id, _repository, _http, _options(), _hostLimiter, GlobalLimiter, _clock, _loggerFactory.CreateLogger<DownloadJob>(), cookies, OnJobStatusChanged);
            _jobs[id] = job;
            job.Start();
            // Remove only this job: a later Start for the same id may already have replaced it.
            _ = job.Completion.ContinueWith(
                finished =>
                {
                    _jobs.TryRemove(new KeyValuePair<long, IDownloadJob>(id, job));
                    if (finished.IsCompletedSuccessfully && finished.Result?.Status == DownloadStatus.Completed)
                    {
                        TryDeleteSavedCookies(id); // a finished download never needs them again
                    }

                    JobEnded?.Invoke(this, id);
                },
                TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>Waits until the download's current job ends and returns the final record.</summary>
    public async Task<DownloadItem?> WaitAsync(long id, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            await job.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return _repository.Get(id);
    }

    /// <summary>Stops a running download after checkpointing; a waiting one becomes paused.</summary>
    public async Task PauseAsync(long id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.Cancel();
            await job.Completion.ConfigureAwait(false);
            return;
        }

        var item = _repository.Get(id);
        if (item is { Status: DownloadStatus.Queued })
        {
            item.Status = DownloadStatus.Paused;
            _repository.Update(item);
            StatusChanged?.Invoke(this, item.Clone());
        }
    }

    public Task PauseAllAsync() => Task.WhenAll(_jobs.Keys.Select(PauseAsync));

    /// <summary>Stops a running download and puts it back in its queue (used when a queue stops).</summary>
    public async Task RequeueAsync(long id)
    {
        await PauseAsync(id).ConfigureAwait(false);
        var item = _repository.Get(id);
        if (item is { Status: DownloadStatus.Paused })
        {
            item.Status = DownloadStatus.Queued;
            _repository.Update(item);
            StatusChanged?.Invoke(this, item.Clone());
        }
    }

    /// <summary>Writes user edits (queue, category, order, limits) for a download that is not running.</summary>
    public void Update(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _repository.Update(item);
        StatusChanged?.Invoke(this, item.Clone());
    }

    /// <summary>Removes the record and its partial file; the finished file only when asked (§9.6).</summary>
    public async Task DeleteAsync(long id, bool deleteFile)
    {
        await PauseAsync(id).ConfigureAwait(false);
        var item = _repository.Get(id);
        if (item is null)
        {
            return;
        }

        TryDeleteFile(item.PartPath);
        if (item.IsMedia)
        {
            TryDeleteFolder(item.MediaPartsFolder);
        }

        if (deleteFile && item.Status == DownloadStatus.Completed)
        {
            TryDeleteFile(item.DestPath);
        }

        _cookies.TryRemove(id, out _);
        _repository.Delete(id);
        _repository.AddHistoryEvent(null, "deleted", item.FileName);
        item.Status = DownloadStatus.Cancelled;
        StatusChanged?.Invoke(this, item);
    }

    public DownloadProgress? GetProgress(long id) => _jobs.TryGetValue(id, out var job) ? job.Snapshot() : null;

    /// <summary>Startup recovery (§11.3): interrupted downloads become paused, not auto-resumed.</summary>
    public IReadOnlyList<long> RecoverInterrupted() => _repository.MarkInterruptedAsPaused();

    public void Dispose()
    {
        foreach (var job in _jobs.Values)
        {
            job.Cancel();
        }

        try
        {
            Task.WaitAll([.. _jobs.Values.Select(j => j.Completion)], TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }

        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// The user switched "remember auth": on, the cookies of unfinished downloads in memory are
    /// saved now; off, every saved cookie is deleted.
    /// </summary>
    public void SyncSavedCookies(bool remember)
    {
        if (_cookieStore is null)
        {
            return;
        }

        if (!remember)
        {
            _cookieStore.DeleteAll();
            return;
        }

        foreach (var (id, cookies) in _cookies)
        {
            if (_repository.Get(id) is { Status: not DownloadStatus.Completed })
            {
                TrySaveCookies(id, cookies);
            }
        }
    }

    private void TrySaveCookies(long id, string cookies)
    {
        try
        {
            _cookieStore?.Save(id, cookies);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Remembering is a convenience; the download itself has the cookies in memory.
            LogCookieStoreFailed(ex, id);
        }
    }

    private string? TryLoadCookies(long id)
    {
        try
        {
            return _cookieStore?.Load(id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCookieStoreFailed(ex, id);
            return null;
        }
    }

    private void TryDeleteSavedCookies(long id)
    {
        try
        {
            _cookieStore?.Delete(id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCookieStoreFailed(ex, id);
        }
    }

    private void OnJobStatusChanged(DownloadItem item) => StatusChanged?.Invoke(this, item);

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} added as {FileName}")]
    private partial void LogAdded(long id, string fileName);

    // The exception never carries cookie values: DPAPI and SQLite errors describe the operation only.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id}: the remembered sign-in could not be read or written")]
    private partial void LogCookieStoreFailed(Exception ex, long id);
}
