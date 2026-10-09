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
internal sealed partial class DownloadJob : IDisposable
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

    private DownloadItem _item = null!;
    private List<Segment> _segments = [];
    private FileStream? _file;
    private long _lastCheckpointBytes;
    private long _lastCheckpointTicks;
    private int _activeConnections;

    public DownloadJob(
        long id,
        IDownloadRepository repository,
        HttpClient http,
        EngineOptions options,
        IClock clock,
        ILogger logger,
        string? cookies,
        Action<DownloadItem> onStatusChanged)
    {
        _id = id;
        _repository = repository;
        _http = http;
        _options = options;
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

    public void Dispose() => _cancellation.Dispose();

    public DownloadProgress Snapshot()
    {
        var segments = _segments.Select(s => s.Snapshot()).ToList();
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
            _file = OpenPartFile(FileMode.Create);
            if (probe.TotalBytes is { } total)
            {
                PartFile.Preallocate(_file, total);
            }

            _repository.SaveSegments(_id, _segments);
        }

        _item.Connections = Math.Max(1, _segments.Count);
        _item.DownloadedBytes = _segments.Sum(s => s.Downloaded);
        _item.StartedAt ??= _clock.UtcNow;
        _lastCheckpointBytes = _item.DownloadedBytes;
        _lastCheckpointTicks = Stopwatch.GetTimestamp();
        SetStatus(DownloadStatus.Downloading);
        _repository.AddHistoryEvent(_id, resume ? "resumed" : "started", resume ? _item.DownloadedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        LogStarted(_id, LogRedactor.Url(_item.FinalUrl), _item.TotalBytes, _segments.Count, resume);
    }

    /// <summary>Phase 2: a single connection per download.</summary>
    private static int PlannedConnections() => 1;

    private async Task TransferAsync(CancellationToken ct)
    {
        var url = new Uri(_item.FinalUrl ?? _item.Url);
        var context = RequestContext.For(_item, _options.UserAgent, _cookies);
        var worker = new SegmentWorker(_http, _options, _file!.SafeFileHandle);

        using var stopCheckpoints = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var checkpoints = CheckpointLoopAsync(stopCheckpoints.Token);
        try
        {
            foreach (var segment in _segments.Where(s => !s.IsComplete))
            {
                segment.Status = SegmentStatus.Active;
                Interlocked.Increment(ref _activeConnections);
                try
                {
                    await worker.RunAsync(url, context, segment, _item.SupportsRanges, ct).ConfigureAwait(false);
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
        finally
        {
            await stopCheckpoints.CancelAsync().ConfigureAwait(false);
            try
            {
                await checkpoints.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
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
        _item.Status = status;
        _repository.Update(_item);
        _onStatusChanged(_item.Clone());
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id}: the remote file changed since the last attempt; starting over")]
    private partial void LogRemoteChanged(long id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id} segment {Index}: checkpoint claimed {Claimed} bytes but only {OnDisk} fit in the file; rewound")]
    private partial void LogReconciled(long id, int index, long claimed, long onDisk);
}
