using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OB2.Core.Downloads;
using OB2.Core.Files;
using OB2.Core.Http;
using OB2.Core.Integrity;
using OB2.Core.Logging;
using OB2.Core.Time;

namespace OB2.Core.Engine;

/// <summary>
/// Runs one download from probe to rename. Owns the partial file while running and is the
/// only writer of its download row until it finishes.
/// </summary>
internal sealed partial class DownloadJob : IDownloadJob, IDisposable
{
    private readonly long _id;
    private readonly IDownloadRepository _repository;
    private readonly HttpClient _http;
    private readonly EngineOptions _options;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly string? _cookies;
    private readonly Action<DownloadItem> _onStatusChanged;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SpeedMeter _speed = new();
    private readonly object _checkpointLock = new();
    private readonly object _statusLock = new();
    private readonly HostConnectionLimiter _hostLimiter;
    private readonly RetryPolicy _retry;
    private readonly ByteCounter _received;

    /// <summary>
    /// D-90: taken to split a segment and by each connection to reserve and count what it writes
    /// (never held across I/O). Splitting also holds <see cref="_checkpointLock"/> first, so a
    /// checkpoint records segments either before a split or after it, never in between.
    /// </summary>
    private readonly object _splitLock = new();
    private TokenBucket[] _limiters;

    private DownloadItem _item = null!;

    /// <summary>Replaced, never changed in place, when a segment is split off (D-90), so readers can enumerate it freely.</summary>
    private List<Segment> _segments = [];
    private FileStream? _file;
    private long _lastCheckpointBytes;
    private long _lastCheckpointTicks;
    private int _activeConnections;
    private int _connectionLimit = 1;
    private int _throttleErrors;

    /// <summary>D-90: ranges and a known size, so idle connections may take over half of a segment.</summary>
    private bool _canSplit;
    private ConnectionTuner? _tuner;

    /// <summary>D-90: starts workers up to the connection limit in the pass running now; null between passes.</summary>
    private Action? _startWorkers;

    /// <summary>The address segments download from; replaced when an expired signed URL is refreshed.</summary>
    private Uri _url = null!;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private int _urlRefreshes;

    private const int MaxUrlRefreshes = DownloadEngine.MaxUrlRefreshes;

    public DownloadJob(
        long id,
        IDownloadRepository repository,
        HttpClient http,
        EngineOptions options,
        HostConnectionLimiter hostLimiter,
        TokenBucket globalLimiter,
        ByteCounter received,
        IClock clock,
        ILogger logger,
        string? cookies,
        Action<DownloadItem> onStatusChanged)
    {
        _id = id;
        _repository = repository;
        _http = http;
        _options = options;
        _hostLimiter = hostLimiter;
        _received = received;
        _limiters = [globalLimiter];
        _retry = new RetryPolicy(options.MaxRetries, options.RetryBaseDelay, options.RetryMaxDelay);
        _clock = clock;
        _logger = logger;
        _cookies = cookies;
        _onStatusChanged = onStatusChanged;
    }

    public long Id => _id;

    public Task<DownloadItem> Completion { get; private set; } = Task.FromResult<DownloadItem>(null!);

    public void Start() => Completion = Task.Run(RunAsync);

    /// <summary>Requests a pause. Safe to call after the job has finished.</summary>
    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _refreshLock.Dispose();
    }

    public DownloadProgress Snapshot()
    {
        // In file order: a segment split off another (D-90) is listed where its bytes go.
        var segments = _segments.OrderBy(s => s.Start).Select(s => s.Snapshot()).ToList();
        var item = _item;
        return new DownloadProgress(
            _id,
            item?.Status ?? DownloadStatus.Probing,
            item?.TotalBytes,
            segments.Sum(s => s.Downloaded),
            _speed.BytesPerSecond,
            Volatile.Read(ref _activeConnections),
            segments);
    }

    private async Task<DownloadItem> RunAsync()
    {
        var ct = _cancellation.Token;
        _item = _repository.Get(_id) ?? throw new InvalidOperationException($"Download {_id} does not exist.");

        try
        {
            await PrepareAsync(ct).ConfigureAwait(false);
            await TransferAsync(ct).ConfigureAwait(false);
            await CompleteAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SafeClose();

            // §10.3: taken while paused, because this is exactly the state a later resume has to
            // validate. SafeClose has flushed, so the bytes are on disk. CancellationToken.None:
            // the pause is already happening and the fingerprint must still be recorded.
            await CaptureHeadFingerprintAsync(CancellationToken.None).ConfigureAwait(false);
            SetStatus(DownloadStatus.Paused);
            _repository.AddHistoryEvent(_id, "paused");
            LogPaused(_id, _item.DownloadedBytes);
        }
        catch (Exception ex)
        {
            SafeClose();
            _item.ErrorCode = ErrorCodes.From(ex);
            _item.ErrorMessage = ex.Message;
            SetStatus(DownloadStatus.Failed);
            _repository.AddHistoryEvent(_id, "failed", _item.ErrorCode);
            LogFailed(ex, _id, _item.ErrorCode);
        }
        finally
        {
            CloseFile(checkpoint: false);
            Dispose();
        }

        return _item.Clone();
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        _item.ErrorCode = null;
        _item.ErrorMessage = null;
        SetStatus(DownloadStatus.Probing);

        var context = RequestContext.For(_item, _options.UserAgent, _cookies);
        var probe = await new UrlProber(_http, _options.ConnectTimeout + _options.ReadIdleTimeout)
            .ProbeAsync(new Uri(_item.Url), context, _item.FileName, ct).ConfigureAwait(false);

        _segments = [.. _repository.GetSegments(_id)];
        var resume = _segments.Count > 0 && File.Exists(_item.PartPath);
        if (resume && RemoteChanged(probe))
        {
            LogRemoteChanged(_id);
            resume = false;
        }

        if (resume && !probe.SupportsRanges && _segments.Any(s => s.Downloaded > 0))
        {
            // Progress cannot be kept without range support; start over.
            resume = false;
        }

        // §10.3: with neither an ETag nor a Last-Modified, a file swapped for one of the same
        // length passes every check above and resumes onto different bytes, which corrupts the
        // result silently. Comparing the first bytes catches that at the cost of one request.
        if (resume && !HasValidator(probe) && await HeadChangedAsync(context, ct).ConfigureAwait(false))
        {
            LogRemoteChanged(_id);
            resume = false;
        }

        _item.FinalUrl = probe.FinalUrl.AbsoluteUri;
        _item.SupportsRanges = probe.SupportsRanges;
        _item.ETag = probe.ETag ?? _item.ETag;
        _item.LastModified = probe.LastModified ?? _item.LastModified;

        Directory.CreateDirectory(_item.SaveFolder);
        if (resume)
        {
            _file = OpenPartFile(FileMode.Open);
            Reconcile(_file.Length);
        }
        else
        {
            _item.TotalBytes = probe.TotalBytes;
            _segments = SegmentPlanner.Plan(probe.TotalBytes, probe.SupportsRanges, PlannedConnections(), _options.MinSegmentBytes);
            EnsureDiskSpace(probe.TotalBytes);

            // Parallel segments write far into the file at once; a sparse file avoids NTFS
            // zero-filling everything before the furthest write (decision D-28).
            PartFile.Create(_item.PartPath, sparse: _segments.Count > 1);
            _file = OpenPartFile(FileMode.Open);
            if (probe.TotalBytes is { } total)
            {
                PartFile.Preallocate(_file, total);
            }

            _repository.SaveSegments(_id, _segments);

            // The connections this file can use (fewer for a small one); a resume keeps it, even
            // when splits have since made more segments than that (D-90).
            _item.Connections = Math.Max(1, _segments.Count);
        }

        // D-90: with ranges and a known size, idle connections split segments, so a resume runs
        // on all its connections again rather than one per unfinished segment.
        _canSplit = _options.SplitSegments && _item.SupportsRanges && _item.TotalBytes is not null && _segments.All(s => !s.IsOpenEnded);
        _connectionLimit = _canSplit
            ? PlannedConnections()
            : Math.Max(1, _segments.Count(s => !s.IsComplete));
        if (_item.SpeedLimitBps is > 0)
        {
            // FR-9: a per-download limit applies on top of the global one.
            _limiters = [.. _limiters, new TokenBucket(_item.SpeedLimitBps)];
        }
        _item.DownloadedBytes = _segments.Sum(s => s.Downloaded);
        _item.StartedAt ??= _clock.UtcNow;
        _lastCheckpointBytes = _item.DownloadedBytes;
        _lastCheckpointTicks = Stopwatch.GetTimestamp();
        SetStatus(DownloadStatus.Downloading);
        _repository.AddHistoryEvent(_id, resume ? "resumed" : "started", resume ? _item.DownloadedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        LogStarted(_id, LogRedactor.Url(_item.FinalUrl), _item.TotalBytes, _segments.Count, resume);
    }

    private int PlannedConnections() => Math.Clamp(_item.Connections, 1, 32);

    /// <summary>
    /// Runs the segment pool. If the server turns out not to honour ranges, falls back once
    /// to a single connection from byte zero (FR-4).
    /// </summary>
    private async Task TransferAsync(CancellationToken ct)
    {
        var fellBack = false;
        while (true)
        {
            try
            {
                await RunPoolAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (RangeNotSupportedException ex) when (!fellBack && !ct.IsCancellationRequested)
            {
                fellBack = true;
                LogRangeFallback(_id, ex.Message);
                FallBackToSingleConnection();
            }
        }
    }

    private async Task RunPoolAsync(CancellationToken ct)
    {
        Volatile.Write(ref _url, new Uri(_item.FinalUrl ?? _item.Url));
        var context = RequestContext.For(_item, _options.UserAgent, _cookies);
        var worker = new SegmentWorker(_http, _options, _file!.SafeFileHandle, _limiters, OnBytes, _splitLock);
        _tuner = _canSplit && _options.AutoConnections
            ? new ConnectionTuner(Volatile.Read(ref _connectionLimit), Math.Min(Settings.DownloadSettings.MaxConnections, _options.MaxConnectionsPerHost))
            : null;

        using var stopLoops = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var checkpoints = CheckpointLoopAsync(stopLoops.Token);
        var tuning = TuneLoopAsync(stopLoops.Token);
        try
        {
            // A worker shed by the adaptive limit re-queues its segment; another pass picks it up.
            while (_segments.Any(s => !s.IsComplete))
            {
                // A pause that lands before this pass starts its workers must end the job here:
                // no worker would be started to notice it, and the pass would begin again for ever.
                ct.ThrowIfCancellationRequested();
                var pending = new ConcurrentQueue<Segment>(_segments.Where(s => !s.IsComplete).OrderBy(s => s.Start));
                if (!_canSplit)
                {
                    // Without splitting, a worker without a segment of its own would only wait.
                    Volatile.Write(ref _connectionLimit, Math.Min(Math.Max(1, _connectionLimit), pending.Count));
                }

                using var failFast = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var workers = new List<Task>();
                var closed = false;
                void StartWorkers()
                {
                    lock (workers)
                    {
                        while (!closed && workers.Count < Volatile.Read(ref _connectionLimit) && !failFast.IsCancellationRequested)
                        {
                            workers.Add(WorkerLoopAsync(workers.Count, pending, worker, context, failFast));
                        }
                    }
                }

                Volatile.Write(ref _startWorkers, StartWorkers);
                try
                {
                    StartWorkers();

                    // The tuner may add workers while these run; the pass ends only once no
                    // worker it started is still running, and then it takes no new ones.
                    while (true)
                    {
                        Task[] running;
                        lock (workers)
                        {
                            running = [.. workers];
                        }

                        await Task.WhenAll(running).ConfigureAwait(false);
                        lock (workers)
                        {
                            if (workers.Count == running.Length)
                            {
                                closed = true;
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    Volatile.Write(ref _startWorkers, null);
                    Task[] all;
                    lock (workers)
                    {
                        closed = true;
                        all = [.. workers];
                    }

                    // After a failure the others are being cancelled (failFast); none may still be
                    // writing when the file closes. Their errors are not this one's to report.
                    await Task.WhenAll(all).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        finally
        {
            await stopLoops.CancelAsync().ConfigureAwait(false);
            foreach (var loop in new[] { checkpoints, tuning })
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task WorkerLoopAsync(int index, ConcurrentQueue<Segment> pending, SegmentWorker worker, RequestContext context, CancellationTokenSource failFast)
    {
        var ct = failFast.Token;
        using var slot = new HostSlot(_hostLimiter);
        try
        {
            while (index < Volatile.Read(ref _connectionLimit))
            {
                // A connection to the server first (§16.1, shared by all downloads), so a piece
                // split off below never waits for one while its donor could have fetched it.
                await slot.EnsureAsync(Volatile.Read(ref _url), ct).ConfigureAwait(false);
                var segment = pending.TryDequeue(out var queued) ? queued : TrySplit();
                if (segment is null)
                {
                    return; // nothing left that is worth a connection of its own
                }

                if (!await RunSegmentWithRetriesAsync(index, segment, worker, context, slot, ct).ConfigureAwait(false))
                {
                    pending.Enqueue(segment);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One segment failed for good: stop the others and report this error.
            await failFast.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// D-90: a new segment made of the second half of the one with the most bytes left, for a
    /// connection with nothing to do; null when nothing is worth splitting. Atomic with respect
    /// to checkpoints, so a crash at any moment resumes with segments that cover the file.
    /// </summary>
    private Segment? TrySplit()
    {
        if (!_canSplit)
        {
            return null;
        }

        lock (_checkpointLock)
        {
            if (_file is null)
            {
                return null; // pausing or closing
            }

            Segment? piece;
            long donorEnd;
            lock (_splitLock)
            {
                var segments = _segments;
                piece = SegmentSplitter.TrySplit(segments, MinPieceBytes, segments.Max(s => s.Index) + 1);
                if (piece is null)
                {
                    return null;
                }

                donorEnd = piece.Start - 1;
                _segments = [.. segments, piece];
            }

            LogSplit(_id, piece.Index, piece.Start, piece.End, donorEnd);
            return piece;
        }
    }

    /// <summary>
    /// D-90: the smallest piece split off, and the least a donor keeps: twice the smallest planned
    /// segment (1 MB by default). Smaller pieces would cost a new connection for too few bytes.
    /// </summary>
    private long MinPieceBytes => Math.Max(2 * _options.MinSegmentBytes, 2L * _options.BufferSize);

    /// <summary>
    /// D-90: every <see cref="EngineOptions.TuneInterval"/>, measures how fast all downloads
    /// receive and lets <see cref="ConnectionTuner"/> decide whether this one gets more
    /// connections, keeps them, or goes back to what it had.
    /// </summary>
    private async Task TuneLoopAsync(CancellationToken ct)
    {
        if (_tuner is not { } tuner || tuner.Settled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.TuneInterval);
        var lastBytes = _received.Total;
        var lastTicks = Stopwatch.GetTimestamp();
        while (!tuner.Settled && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var bytes = _received.Total;
            var seconds = Stopwatch.GetElapsedTime(lastTicks).TotalSeconds;
            var speed = seconds > 0 ? (bytes - lastBytes) / seconds : 0;
            lastBytes = bytes;
            lastTicks = Stopwatch.GetTimestamp();

            var before = Volatile.Read(ref _connectionLimit);
            var after = tuner.Next(speed, CanGrow(before));
            if (after == before || (after > before && tuner.Settled))
            {
                continue; // nothing to do, or the server pushed back meanwhile: never add then
            }

            // Only from the number this decision was based on: if throttling cut the connections
            // since (NoteThrottle), that cut stands and tuning is over.
            if (Interlocked.CompareExchange(ref _connectionLimit, after, before) != before)
            {
                return;
            }

            LogConnectionsTuned(_id, before, after, speed / (1024 * 1024));
            if (after > before)
            {
                Volatile.Read(ref _startWorkers)?.Invoke();
            }
        }
    }

    /// <summary>D-90: whether more connections could do anything for this download now.</summary>
    private bool CanGrow(int limit)
    {
        if (_limiters.Any(l => l.IsLimited))
        {
            return false; // a speed limit decides the speed, not the connections
        }

        // Enough left for every new connection to get a few pieces of its own.
        var left = _segments.Sum(s => Math.Max(0, SegmentSplitter.Left(s)));
        if (left < (limit + ConnectionTuner.StepFrom(limit)) * MinPieceBytes * 4)
        {
            return false;
        }

        return _hostLimiter.Available(Volatile.Read(ref _url)) > 0;
    }

    /// <returns>True when the segment finished; false when this worker was shed by the adaptive limit.</returns>
    private async Task<bool> RunSegmentWithRetriesAsync(int index, Segment segment, SegmentWorker worker, RequestContext context, HostSlot slot, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var url = Volatile.Read(ref _url);
            await slot.EnsureAsync(url, ct).ConfigureAwait(false);
            var before = segment.Downloaded;
            segment.Status = SegmentStatus.Active;
            Interlocked.Increment(ref _activeConnections);
            try
            {
                await worker.RunAsync(url, context, segment, _item.SupportsRanges, ct).ConfigureAwait(false);
                segment.RetryCount = 0;
                return true;
            }
            catch (HttpStatusException ex) when (!ct.IsCancellationRequested && IsExpiredLinkStatus(ex.StatusCode))
            {
                // §16.1: a signed CDN URL can expire mid-download. Ask the original link for a fresh
                // one; if it gives the same file at a new address, carry on without spending a retry.
                segment.Status = SegmentStatus.Pending;
                if (!await TryRefreshUrlAsync(url, context, ct).ConfigureAwait(false))
                {
                    segment.Status = SegmentStatus.Failed;
                    throw;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && RetryPolicy.IsTransient(ex))
            {
                var progressed = segment.Downloaded > before;
                segment.Status = SegmentStatus.Pending;
                var throttled = RetryPolicy.IsThrottling(ex) || (!progressed && ex is HttpRequestException);
                if (throttled)
                {
                    NoteThrottle();
                }

                if (index >= Volatile.Read(ref _connectionLimit))
                {
                    // Shed by the adaptive limit: hand the segment back without spending a retry.
                    return false;
                }

                segment.RetryCount = progressed ? 1 : segment.RetryCount + 1;
                if (segment.RetryCount > _retry.MaxRetries)
                {
                    segment.Status = SegmentStatus.Failed;
                    throw;
                }

                EnterRetrying();
                var delay = _retry.DelayFor(segment.RetryCount, ex);
                LogRetry(_id, segment.Index, segment.RetryCount, _retry.MaxRetries, delay.TotalMilliseconds, ex.Message);
                slot.Release(); // other downloads from this server may use it while this one waits
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                segment.Status = SegmentStatus.Failed;
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnections);
                if (segment.Status == SegmentStatus.Active)
                {
                    segment.Status = SegmentStatus.Pending;
                }
            }
        }
    }

    /// <summary>§16.1: after repeated throttling errors, halve this download's connections (minimum one).</summary>
    private void NoteThrottle()
    {
        _tuner?.Throttled(); // D-90: a server that pushes back never gets more connections
        if (Interlocked.Increment(ref _throttleErrors) < 3)
        {
            return;
        }

        Interlocked.Exchange(ref _throttleErrors, 0);
        int current, reduced;
        do
        {
            current = Volatile.Read(ref _connectionLimit);
            reduced = Math.Max(1, current / 2);
        }
        while (current > 1 && Interlocked.CompareExchange(ref _connectionLimit, reduced, current) != current);

        if (current > 1)
        {
            LogConnectionsReduced(_id, current, reduced);
        }
    }

    private static bool IsExpiredLinkStatus(System.Net.HttpStatusCode status) => status is
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or
        System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone;

    /// <summary>
    /// Re-probes the original link for a fresh final URL (decision D-66). Segments that fail at
    /// the same moment share one refresh. Returns false when there is nothing new to try: the
    /// link gives the same address, now points at a different file, cannot be probed, or has
    /// been refreshed <see cref="MaxUrlRefreshes"/> times already.
    /// </summary>
    private async Task<bool> TryRefreshUrlAsync(Uri failedUrl, RequestContext context, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Volatile.Read(ref _url).Equals(failedUrl))
            {
                return true; // another segment already fetched a fresh address
            }

            if (_urlRefreshes >= MaxUrlRefreshes)
            {
                return false;
            }

            _urlRefreshes++;
            ProbeResult probe;
            try
            {
                probe = await new UrlProber(_http, _options.ConnectTimeout + _options.ReadIdleTimeout)
                    .ProbeAsync(new Uri(_item.Url), context, _item.FileName, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpStatusException or HttpRequestException or TimeoutException or IOException)
            {
                LogUrlRefreshFailed(_id, ex.Message);
                return false;
            }

            if (probe.FinalUrl.Equals(failedUrl))
            {
                return false; // same address: the server really refuses
            }

            if (RemoteChanged(probe))
            {
                // A different file now; failing keeps progress, and the next start notices the change.
                LogRemoteChanged(_id);
                return false;
            }

            Volatile.Write(ref _url, probe.FinalUrl);
            lock (_statusLock)
            {
                _item.FinalUrl = probe.FinalUrl.AbsoluteUri;
                _repository.Update(_item);
            }

            _repository.AddHistoryEvent(_id, "url-refreshed");
            LogUrlRefreshed(_id, _urlRefreshes);
            return true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void FallBackToSingleConnection()
    {
        lock (_checkpointLock)
        {
            _item.SupportsRanges = false;
            _item.Connections = 1;
            _connectionLimit = 1;
            _canSplit = false;
            _tuner?.Throttled();
            _segments = SegmentPlanner.Plan(_item.TotalBytes, supportsRanges: false, 1, _options.MinSegmentBytes);
            _file!.SetLength(0);
            if (_item.TotalBytes is { } total)
            {
                PartFile.Preallocate(_file, total);
            }

            _repository.SaveSegments(_id, _segments);
            _repository.Checkpoint(_id, 0, _segments);
            _item.DownloadedBytes = 0;
            _lastCheckpointBytes = 0;
        }

        SetStatus(DownloadStatus.Downloading);
    }

    private void OnBytes(int bytes)
    {
        _received.Add(bytes);
        if (_item.Status == DownloadStatus.Retrying)
        {
            lock (_statusLock)
            {
                if (_item.Status == DownloadStatus.Retrying)
                {
                    SetStatus(DownloadStatus.Downloading);
                }
            }
        }
    }

    private void EnterRetrying()
    {
        lock (_statusLock)
        {
            if (_item.Status == DownloadStatus.Downloading)
            {
                SetStatus(DownloadStatus.Retrying);
            }
        }
    }

    private async Task CompleteAsync(CancellationToken ct)
    {
        var downloaded = _segments.Sum(s => s.Downloaded);
        if (_item.TotalBytes is { } expected && downloaded != expected)
        {
            throw new DownloadFailedException(ErrorCodes.SizeMismatch, $"Expected {expected:N0} bytes but received {downloaded:N0}.");
        }

        if (_item.TotalBytes is null)
        {
            _item.TotalBytes = downloaded;
            _file!.SetLength(downloaded);
        }

        CloseFile(checkpoint: true);
        PartFile.TryClearSparse(_item.PartPath);

        if (Checksum.TryCreate(_item.ChecksumAlgo, _item.ChecksumValue, out var checksum))
        {
            var actual = await Checksum.ComputeAsync(_item.PartPath, checksum!.Algorithm, ct).ConfigureAwait(false);
            if (!string.Equals(actual, checksum.HexValue, StringComparison.OrdinalIgnoreCase))
            {
                // The .pgpart file is kept for inspection, never renamed (Phase 2 exit criterion).
                throw new DownloadFailedException(
                    ErrorCodes.ChecksumMismatch,
                    $"{checksum.AlgorithmName.ToUpperInvariant()} mismatch: expected {checksum.HexValue}, got {actual}. The partial file was kept.");
            }

            _repository.AddHistoryEvent(_id, "verified", checksum.AlgorithmName);
        }

        var destination = _item.DestPath;
        if (File.Exists(destination))
        {
            destination = UniquePath.Allocate(destination);
        }

        SafePath.EnsureInside(_item.SaveFolder, destination);
        File.Move(_item.PartPath, destination, overwrite: false);
        _item.DestPath = destination;
        _item.FileName = Path.GetFileName(destination);
        MarkOfTheWeb.TryApply(destination, _item.FinalUrl ?? _item.Url, _item.Referrer);

        foreach (var segment in _segments)
        {
            segment.Status = SegmentStatus.Done;
        }

        _repository.Checkpoint(_id, downloaded, _segments);
        _item.DownloadedBytes = downloaded;
        _item.CompletedAt = _clock.UtcNow;
        SetStatus(DownloadStatus.Completed);
        _repository.AddHistoryEvent(_id, "completed", destination);
        LogCompleted(_id, downloaded);
    }

    /// <summary>
    /// Records a fingerprint of the first bytes of the part file, once, so a later resume can
    /// tell whether the remote file changed. Only taken when the first segment has actually
    /// reached that far: a hash of fewer bytes would never match the range fetched on resume.
    /// </summary>
    private async Task CaptureHeadFingerprintAsync(CancellationToken ct)
    {
        if (_item.HeadSha256 is not null || _segments.Count == 0)
        {
            return;
        }

        // The first segment starts at 0, so its progress is how many leading bytes exist.
        var leading = _segments.OrderBy(s => s.Start).First() is { Start: 0 } first ? first.Downloaded : 0;
        if (leading < HeadFingerprint.Length)
        {
            return;
        }

        if (await HeadFingerprint.OfFileAsync(_item.PartPath, HeadFingerprint.Length, ct).ConfigureAwait(false) is { } print)
        {
            _item.HeadSha256 = print.Hash;
            _item.HeadBytes = print.Bytes;
            _repository.Update(_item);
        }
    }

    /// <summary>Whether the server offers something to compare a resume against.</summary>
    private static bool HasValidator(ProbeResult probe) => probe.ETag is not null || probe.LastModified is not null;

    /// <summary>
    /// Compares the first bytes on disk with the same range fetched now. Returns false whenever
    /// the answer is not clear: a resume is never refused on missing evidence, because the cost
    /// of a wrong "changed" is re-downloading a file that was fine.
    /// </summary>
    private async Task<bool> HeadChangedAsync(RequestContext context, CancellationToken ct)
    {
        var stored = _item.HeadSha256;
        if (stored is null || _item.HeadBytes is not > 0)
        {
            return false; // nothing recorded (an older download, or the first run)
        }

        var storedBytes = _item.HeadBytes.Value;

        try
        {
            using var request = DownloadHttpClient.CreateRequest(HttpMethod.Get, new Uri(_item.Url), context);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, storedBytes - 1);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                return false; // the server ignored the range; nothing to compare
            }

            var buffer = new byte[storedBytes];
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            if (read < buffer.Length)
            {
                return false;
            }

            var fresh = HeadFingerprint.Of(buffer);
            return HeadFingerprint.Differs(stored, storedBytes, fresh, read);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException && !ct.IsCancellationRequested)
        {
            // The check is a safety net, not a gate: a failure here leaves the resume as it was,
            // and the transfer itself will report any real network problem.
            return false;
        }
    }

    private bool RemoteChanged(ProbeResult probe)
    {
        if (_item.TotalBytes is { } known && probe.TotalBytes is { } now && known != now)
        {
            return true;
        }

        if (_item.ETag is not null && probe.ETag is not null)
        {
            return !string.Equals(_item.ETag, probe.ETag, StringComparison.Ordinal);
        }

        return _item.LastModified is not null && probe.LastModified is not null
            && !string.Equals(_item.LastModified, probe.LastModified, StringComparison.Ordinal);
    }

    /// <summary>
    /// Crash recovery (§11.3): a segment can never claim bytes beyond the end of the file on
    /// disk. Checkpoints are written only after a flush, so normally nothing changes.
    /// </summary>
    private void Reconcile(long fileLength)
    {
        foreach (var segment in _segments)
        {
            var maxOnDisk = Math.Max(0, fileLength - segment.Start);
            if (segment.Downloaded > maxOnDisk)
            {
                LogReconciled(_id, segment.Index, segment.Downloaded, maxOnDisk);
                segment.Downloaded = maxOnDisk;
            }

            if (segment.Length is { } length && segment.Downloaded > length)
            {
                segment.Downloaded = length;
            }

            segment.Status = segment.IsComplete && segment.Length is not null ? SegmentStatus.Done : SegmentStatus.Pending;
        }

        if (_item.TotalBytes is { } total && fileLength < total)
        {
            PartFile.Preallocate(_file!, total);
        }
    }

    private void EnsureDiskSpace(long? totalBytes)
    {
        if (totalBytes is not { } needed)
        {
            return;
        }

        var root = Path.GetPathRoot(Path.GetFullPath(_item.DestPath));
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        try
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needed)
            {
                throw new DownloadFailedException(ErrorCodes.DiskFull, $"The file needs {needed:N0} bytes but only {free:N0} are free on {root}.");
            }
        }
        catch (ArgumentException)
        {
            // UNC paths and unusual mounts: skip the check rather than block the download.
        }
    }

    private FileStream OpenPartFile(FileMode mode) =>
        new(_item.PartPath, mode, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0, FileOptions.Asynchronous);

    private async Task CheckpointLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var downloaded = _segments.Sum(s => s.Downloaded);
            _speed.Sample(downloaded);
            var due = downloaded - _lastCheckpointBytes >= _options.CheckpointBytes
                || (downloaded != _lastCheckpointBytes && Stopwatch.GetElapsedTime(_lastCheckpointTicks) >= _options.CheckpointInterval);
            if (due)
            {
                Checkpoint();
            }
        }
    }

    /// <summary>Flush file data to disk first, then record progress, so the record never runs ahead of the disk.</summary>
    private void Checkpoint()
    {
        lock (_checkpointLock)
        {
            if (_file is null)
            {
                return;
            }

            var snapshot = _segments.Select(s => s.Snapshot()).ToList();
            _file.Flush(flushToDisk: true);
            var downloaded = snapshot.Sum(s => s.Downloaded);
            _repository.Checkpoint(_id, downloaded, snapshot);
            _item.DownloadedBytes = downloaded;
            _lastCheckpointBytes = downloaded;
            _lastCheckpointTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Final checkpoint on pause or failure; a failing disk must not hide the original error.</summary>
    private void SafeClose()
    {
        try
        {
            CloseFile(checkpoint: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogCheckpointFailed(ex, _id);
        }
    }

    private void CloseFile(bool checkpoint)
    {
        lock (_checkpointLock)
        {
            if (_file is null)
            {
                return;
            }

            try
            {
                if (checkpoint)
                {
                    Checkpoint();
                }
            }
            finally
            {
                _file.Dispose();
                _file = null;
            }
        }
    }

    private void SetStatus(DownloadStatus status)
    {
        lock (_statusLock)
        {
            _item.Status = status;
            _repository.Update(_item);
            _onStatusChanged(_item.Clone());
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id}: {Reason} Falling back to one connection from the start.")]
    private partial void LogRangeFallback(long id, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} segment {Index}: retry {Attempt}/{Max} in {DelayMs:0} ms after: {Error}")]
    private partial void LogRetry(long id, int index, int attempt, int max, double delayMs, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id}: server is throttling; connections reduced from {From} to {To}")]
    private partial void LogConnectionsReduced(long id, int from, int to);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Download {Id}: segment {Index} ({Start}-{End}) split off; the donor now ends at {DonorEnd}")]
    private partial void LogSplit(long id, int index, long start, long end, long donorEnd);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id}: connections {From} -> {To} at {MegabytesPerSecond:0.0} MB/s")]
    private partial void LogConnectionsTuned(long id, int from, int to, double megabytesPerSecond);

    /// <summary>
    /// D-90: one worker's place in the per-server limit (§16.1). Held from one segment to the next,
    /// so a split piece never waits for a connection; given up while waiting to retry, and taken
    /// again for another server when a refreshed link points elsewhere.
    /// </summary>
    private sealed class HostSlot(HostConnectionLimiter limiter) : IDisposable
    {
        private IDisposable? _held;
        private string? _host;

        public async Task EnsureAsync(Uri url, CancellationToken cancellationToken)
        {
            var host = HostConnectionLimiter.KeyOf(url);
            if (_held is not null && _host == host)
            {
                return;
            }

            Release();
            _held = await limiter.AcquireAsync(url, cancellationToken).ConfigureAwait(false);
            _host = host;
        }

        public void Release()
        {
            _held?.Dispose();
            _held = null;
            _host = null;
        }

        public void Dispose() => Release();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} started: {Url}, {Total} bytes, {Segments} segment(s), resume={Resume}")]
    private partial void LogStarted(long id, string url, long? total, int segments, bool resume);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} paused at {Bytes} bytes")]
    private partial void LogPaused(long id, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} completed, {Bytes} bytes")]
    private partial void LogCompleted(long id, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id} failed with {Code}")]
    private partial void LogFailed(Exception ex, long id, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Download {Id}: final checkpoint failed")]
    private partial void LogCheckpointFailed(Exception ex, long id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id}: the download address expired; continuing with a fresh one (refresh {Count})")]
    private partial void LogUrlRefreshed(long id, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id}: could not get a fresh download address: {Error}")]
    private partial void LogUrlRefreshFailed(long id, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id}: the remote file changed since the last attempt; starting over")]
    private partial void LogRemoteChanged(long id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id} segment {Index}: checkpoint claimed {Claimed} bytes but only {OnDisk} fit in the file; rewound")]
    private partial void LogReconciled(long id, int index, long claimed, long onDisk);
}
