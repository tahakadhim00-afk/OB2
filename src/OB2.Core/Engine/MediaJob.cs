using Microsoft.Extensions.Logging;
using OB2.Core.Downloads;
using OB2.Core.Files;
using OB2.Core.Logging;
using OB2.Core.Media;
using OB2.Core.Time;

namespace OB2.Core.Engine;

/// <summary>
/// Downloads a video page through yt-dlp (D-71): runs it with the chosen formats, turns its
/// progress lines into engine progress, and records the merged file. Pausing stops the process;
/// yt-dlp keeps its parts and continues them on the next run.
/// </summary>
internal sealed partial class MediaJob : IDownloadJob, IDisposable
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(5);
    private const int KeptErrorLines = 50;

    private readonly long _id;
    private readonly IDownloadRepository _repository;
    private readonly MediaTools? _tools;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Action<DownloadItem> _onStatusChanged;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SpeedMeter _speed = new();
    private readonly object _statusLock = new();
    private readonly object _progressLock = new();

    /// <summary>Bytes and total per format id: video and audio are separate downloads in one run.</summary>
    private readonly Dictionary<string, (long Downloaded, long? Total)> _parts = new(StringComparer.Ordinal);
    private readonly Queue<string> _errors = new();
    private DownloadItem _item = null!;
    private long? _estimatedTotal;
    private string? _finalFile;

    public MediaJob(long id, IDownloadRepository repository, MediaTools? tools, IClock clock, ILogger logger, Action<DownloadItem> onStatusChanged)
    {
        _id = id;
        _repository = repository;
        _tools = tools;
        _clock = clock;
        _logger = logger;
        _onStatusChanged = onStatusChanged;
    }

    public Task<DownloadItem> Completion { get; private set; } = Task.FromResult<DownloadItem>(null!);

    public void Start() => Completion = Task.Run(RunAsync);

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
        var item = _item;
        long downloaded;
        long? total;
        lock (_progressLock)
        {
            (downloaded, total) = Totals();
        }

        return new DownloadProgress(_id, item?.Status ?? DownloadStatus.Probing, total ?? item?.TotalBytes, downloaded, _speed.BytesPerSecond, item?.Status == DownloadStatus.Downloading ? 1 : 0, []);
    }

    private async Task<DownloadItem> RunAsync()
    {
        var ct = _cancellation.Token;
        _item = _repository.Get(_id) ?? throw new InvalidOperationException($"Download {_id} does not exist.");
        _estimatedTotal = _item.TotalBytes;

        try
        {
            _item.ErrorCode = null;
            _item.ErrorMessage = null;
            SetStatus(DownloadStatus.Probing);
            if (_tools is null || !_tools.IsInstalled)
            {
                throw new DownloadFailedException(ErrorCodes.ToolsMissing, "yt-dlp, ffmpeg and Deno are not installed. Open the video page from the browser again to install them.");
            }

            Directory.CreateDirectory(_item.SaveFolder);
            var resumed = Directory.Exists(_item.MediaPartsFolder) && Directory.EnumerateFileSystemEntries(_item.MediaPartsFolder).Any();
            Directory.CreateDirectory(_item.MediaPartsFolder);
            _item.StartedAt ??= _clock.UtcNow;
            SetStatus(DownloadStatus.Downloading);
            _repository.AddHistoryEvent(_id, resumed ? "resumed" : "started", _item.MediaFormat);
            LogStarted(_id, LogRedactor.Url(_item.Url), _item.MediaFormat!, resumed);

            using var stopCheckpoints = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var checkpoints = CheckpointLoopAsync(stopCheckpoints.Token);
            int code;
            try
            {
                code = await _tools.Runner.RunAsync(_tools.YtDlpPath, YtDlp.DownloadArguments(_tools, _item), OnOutput, OnError, ct).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new DownloadFailedException(ErrorCodes.ToolsMissing, $"yt-dlp could not be started: {ex.Message}");
            }
            finally
            {
                await stopCheckpoints.CancelAsync().ConfigureAwait(false);
                await checkpoints.ConfigureAwait(false);
            }

            if (code != 0)
            {
                string? message;
                lock (_errors)
                {
                    message = YtDlp.ErrorMessage(_errors);
                }

                throw new DownloadFailedException(ErrorCodes.SiteError, message ?? $"yt-dlp stopped with exit code {code}.");
            }

            Complete();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Checkpoint();
            SetStatus(DownloadStatus.Paused);
            _repository.AddHistoryEvent(_id, "paused");
            LogPaused(_id, _item.DownloadedBytes);
        }
        catch (Exception ex)
        {
            _item.ErrorCode = ErrorCodes.From(ex);
            _item.ErrorMessage = ex.Message;
            SetStatus(DownloadStatus.Failed);
            _repository.AddHistoryEvent(_id, "failed", _item.ErrorCode);
            LogFailed(ex, _id, _item.ErrorCode);
        }
        finally
        {
            Dispose();
        }

        return _item.Clone();
    }

    private void Complete()
    {
        var file = _finalFile is { Length: > 0 } printed ? Path.GetFullPath(printed) : _item.DestPath;
        if (!File.Exists(file) && File.Exists(_item.DestPath))
        {
            file = _item.DestPath; // the name OB2 asked for, if the printed one came back garbled (D-73)
        }

        if (!File.Exists(file))
        {
            throw new DownloadFailedException(ErrorCodes.FileError, "yt-dlp finished, but the file it reported is not there.");
        }

        SafePath.EnsureInside(_item.SaveFolder, file);
        var size = new FileInfo(file).Length;
        _item.DestPath = file;
        _item.FileName = Path.GetFileName(file);
        _item.TotalBytes = size;
        _item.DownloadedBytes = size;
        _item.CompletedAt = _clock.UtcNow;
        MarkOfTheWeb.TryApply(file, _item.Url, _item.Referrer);
        TryDeleteFolder(_item.MediaPartsFolder);
        SetStatus(DownloadStatus.Completed);
        _repository.AddHistoryEvent(_id, "completed", file);
        LogCompleted(_id, size);
    }

    private void OnOutput(string line)
    {
        if (YtDlp.ParseFile(line) is { } file)
        {
            _finalFile = file;
            return;
        }

        if (YtDlp.ParseProgress(line) is not { } progress)
        {
            return;
        }

        long downloaded;
        lock (_progressLock)
        {
            _parts.TryGetValue(progress.FormatId, out var previous);
            var total = progress.Total ?? previous.Total;
            var bytes = progress.Status == "finished" && total is { } t ? t : progress.Downloaded ?? previous.Downloaded;
            _parts[progress.FormatId] = (bytes, total);
            downloaded = Totals().Downloaded;
        }

        _speed.Sample(downloaded);
    }

    private void OnError(string line)
    {
        lock (_errors)
        {
            _errors.Enqueue(line);
            while (_errors.Count > KeptErrorLines)
            {
                _errors.Dequeue();
            }
        }
    }

    /// <summary>
    /// Bytes so far across the formats, and the total: the estimate from the format list until
    /// yt-dlp has reported more than that.
    /// </summary>
    private (long Downloaded, long? Total) Totals()
    {
        var downloaded = _parts.Values.Sum(p => p.Downloaded);
        var reported = _parts.Values.Sum(p => p.Total ?? 0);
        var total = Math.Max(_estimatedTotal ?? 0, reported);
        return (downloaded, total > 0 ? total : null);
    }

    private async Task CheckpointLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(CheckpointInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Checkpoint();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Checkpoint()
    {
        lock (_statusLock)
        {
            lock (_progressLock)
            {
                var (downloaded, total) = Totals();
                _item.DownloadedBytes = downloaded;
                _item.TotalBytes = total ?? _item.TotalBytes;
            }

            _repository.Update(_item);
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

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover parts waste space but do no harm.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Media download {Id} started: {Url}, formats {Format}, resume={Resume}")]
    private partial void LogStarted(long id, string url, string format, bool resume);

    [LoggerMessage(Level = LogLevel.Information, Message = "Media download {Id} paused at {Bytes} bytes")]
    private partial void LogPaused(long id, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Media download {Id} completed, {Bytes} bytes")]
    private partial void LogCompleted(long id, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media download {Id} failed with {Code}")]
    private partial void LogFailed(Exception ex, long id, string code);
}
