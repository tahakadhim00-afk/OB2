using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OB2.Core.Downloads;
using OB2.Core.Files;
using OB2.Core.Http;
using OB2.Core.Logging;
using OB2.Core.Media;
using OB2.Core.Time;

namespace OB2.Core.Engine;

/// <summary>
/// D-80: downloads a stream OB2 read itself (<see cref="StreamSpec"/>), without yt-dlp. It reads
/// the manifest again with the browser's headers, fetches the picked tracks' pieces several at a
/// time into the parts folder, joins each track's pieces in order, and has ffmpeg put picture and
/// sound into the finished file without re-encoding. A piece is written under a temporary name
/// and renamed once complete, so pausing keeps every finished piece and the next run fetches the
/// rest.
/// </summary>
internal sealed partial class StreamJob : IDownloadJob, IDisposable
{
    /// <summary>Players fetch one piece at a time; a few more is quick without looking like an attack.</summary>
    private const int MaxParallelPieces = 6;
    private const int BufferSize = 128 * 1024;
    private const int KeptErrorLines = 20;

    private readonly long _id;
    private readonly IDownloadRepository _repository;
    private readonly HttpClient _http;
    private readonly EngineOptions _options;
    private readonly TokenBucket _globalLimiter;
    private readonly MediaTools? _tools;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly string? _cookies;
    private readonly Action<DownloadItem> _onStatusChanged;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SpeedMeter _speed = new();
    private readonly object _statusLock = new();
    private readonly object _progressLock = new();
    private readonly Dictionary<string, TrackProgress> _progress = new(StringComparer.Ordinal);
    /// <summary>One fetch per key however many pieces wait for it (GetOrAdd alone may run its factory once per caller).</summary>
    private readonly ConcurrentDictionary<Uri, Lazy<Task<byte[]>>> _keys = new();
    private DownloadItem _item = null!;
    private long? _estimatedTotal;
    private long _inFlight;
    private int _active;

    public StreamJob(long id, IDownloadRepository repository, HttpClient http, EngineOptions options, TokenBucket globalLimiter, MediaTools? tools, IClock clock, ILogger logger, string? cookies, Action<DownloadItem> onStatusChanged)
    {
        _id = id;
        _repository = repository;
        _http = http;
        _options = options;
        _globalLimiter = globalLimiter;
        _tools = tools;
        _clock = clock;
        _logger = logger;
        _cookies = cookies;
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

        return new DownloadProgress(_id, item?.Status ?? DownloadStatus.Probing, total ?? item?.TotalBytes, downloaded, _speed.BytesPerSecond, Volatile.Read(ref _active), []);
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
            if (_tools is null || !File.Exists(_tools.FfmpegPath))
            {
                throw new DownloadFailedException(ErrorCodes.ToolsMissing, "ffmpeg is not installed. Open the video page from the browser again to install the video tools.");
            }

            if (!StreamSpec.TryParse(_item.MediaFormat, out var videoId, out var audioId))
            {
                throw new DownloadFailedException(ErrorCodes.Internal, $"\"{_item.MediaFormat}\" is not a stream's quality.");
            }

            var request = new StreamRequest(new Uri(_item.Url), _item.Referrer, _item.UserAgent, _cookies, _item.Headers);
            var reader = new StreamManifestReader(_http, _options.UserAgent, _options.ConnectTimeout + _options.ReadIdleTimeout);
            var tracks = await ReadTracksAsync(reader, request, [videoId, audioId], ct).ConfigureAwait(false);

            Directory.CreateDirectory(_item.SaveFolder);
            var resumed = Directory.Exists(_item.MediaPartsFolder) && Directory.EnumerateFiles(_item.MediaPartsFolder, "*.part").Any();
            Directory.CreateDirectory(_item.MediaPartsFolder);
            var work = Plan(tracks);
            _item.StartedAt ??= _clock.UtcNow;
            SetStatus(DownloadStatus.Downloading);
            _repository.AddHistoryEvent(_id, resumed ? "resumed" : "started", _item.MediaFormat);
            LogStarted(_id, LogRedactor.Url(_item.Url), _item.MediaFormat!, work.Count, resumed);

            using (var stopCheckpoints = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var checkpoints = CheckpointLoopAsync(stopCheckpoints.Token);
                try
                {
                    var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_options.Connections, 1, MaxParallelPieces), CancellationToken = ct };
                    var limiter = new TokenBucket(_item.SpeedLimitBps);
                    await Parallel.ForEachAsync(work.Where(w => !File.Exists(w.Path)), parallel, (w, token) => FetchAsync(w, reader, request, limiter, token)).ConfigureAwait(false);
                }
                finally
                {
                    await stopCheckpoints.CancelAsync().ConfigureAwait(false);
                    await checkpoints.ConfigureAwait(false);
                }
            }

            var inputs = tracks.Select(t => Join(t.Track, work.Where(w => w.Track == t.Track.Id).ToList())).ToList();
            var output = await MergeAsync(inputs, ct).ConfigureAwait(false);
            Complete(output);
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
            _item.ErrorMessage = ex is HttpStatusException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.Gone or HttpStatusCode.NotFound } http
                ? $"{http.Message} The stream's link or its token has probably expired: play the video in the browser again and download it anew from the OB2 button."
                : ex.Message;
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

    /// <summary>The picked tracks, found again by their ids (the manifest's order does not change), with their pieces.</summary>
    private static async Task<List<(StreamTrack Track, IReadOnlyList<StreamPiece> Pieces)>> ReadTracksAsync(StreamManifestReader reader, StreamRequest request, string?[] ids, CancellationToken ct)
    {
        StreamManifest manifest;
        try
        {
            manifest = await reader.ReadAsync(request, ct).ConfigureAwait(false);
        }
        catch (MediaException ex)
        {
            throw new DownloadFailedException(ErrorCodes.SiteError, ex.Message);
        }

        var tracks = new List<(StreamTrack, IReadOnlyList<StreamPiece>)>();
        foreach (var id in ids.OfType<string>())
        {
            var track = manifest.Tracks.FirstOrDefault(t => t.Id == id)
                ?? throw new DownloadFailedException(ErrorCodes.SiteError, "The stream no longer offers the quality picked for this download. Download it anew from the OB2 button.");
            try
            {
                tracks.Add((track, await reader.PiecesAsync(track, request, ct).ConfigureAwait(false)));
            }
            catch (MediaException ex)
            {
                throw new DownloadFailedException(ErrorCodes.SiteError, ex.Message);
            }
        }

        return tracks;
    }

    /// <summary>One file per piece, numbered in playing order; the ones already there are counted as done.</summary>
    private List<Work> Plan(List<(StreamTrack Track, IReadOnlyList<StreamPiece> Pieces)> tracks)
    {
        var work = new List<Work>();
        lock (_progressLock)
        {
            foreach (var (track, pieces) in tracks)
            {
                var progress = _progress[track.Id] = new TrackProgress(pieces.Count);
                for (var i = 0; i < pieces.Count; i++)
                {
                    var path = Path.Combine(_item.MediaPartsFolder, string.Create(CultureInfo.InvariantCulture, $"{track.Id}-{i:D6}.part"));
                    work.Add(new Work(track.Id, pieces[i], path));
                    if (File.Exists(path))
                    {
                        progress.Done++;
                        progress.Bytes += new FileInfo(path).Length;
                    }
                }
            }
        }

        return work;
    }

    private async ValueTask FetchAsync(Work work, StreamManifestReader reader, StreamRequest request, TokenBucket limiter, CancellationToken ct)
    {
        var retry = new RetryPolicy(_options.MaxRetries, _options.RetryBaseDelay, _options.RetryMaxDelay);
        var context = request.Context(_options.UserAgent);
        var temp = work.Path + ".tmp";
        for (var attempt = 1; ; attempt++)
        {
            long got = 0;
            Interlocked.Increment(ref _active);
            try
            {
                using (var message = DownloadHttpClient.CreateRequest(HttpMethod.Get, work.Piece.Url, context))
                {
                    if (work.Piece.Offset is { } offset)
                    {
                        message.Headers.Range = new RangeHeaderValue(offset, work.Piece.Length is { } length ? offset + length - 1 : null);
                    }

                    using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw HttpStatusException.From(response);
                    }

                    if (work.Piece.Offset is > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        throw new DownloadFailedException(ErrorCodes.RangeNotSupported, "The stream's server ignores byte ranges, which its pieces need.");
                    }

                    await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                    var buffer = new byte[BufferSize];
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    while (true)
                    {
                        idle.CancelAfter(_options.ReadIdleTimeout);
                        int read;
                        try
                        {
                            read = await body.ReadAsync(buffer.AsMemory(0, limiter.SuggestedChunk(_globalLimiter.SuggestedChunk(BufferSize))), idle.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new TimeoutException($"The stream's server sent nothing for {_options.ReadIdleTimeout.TotalSeconds:0} seconds.");
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        await limiter.WaitAsync(read, ct).ConfigureAwait(false);
                        await _globalLimiter.WaitAsync(read, ct).ConfigureAwait(false);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        got += read;
                        Interlocked.Add(ref _inFlight, read);
                    }
                }

                if (work.Piece.Key is { } key)
                {
                    await DecryptAsync(temp, key, reader, request, ct).ConfigureAwait(false);
                }

                File.Move(temp, work.Path, overwrite: true);
                lock (_progressLock)
                {
                    var progress = _progress[work.Track];
                    progress.Done++;
                    progress.Bytes += new FileInfo(work.Path).Length;
                }

                Interlocked.Add(ref _inFlight, -got);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt <= retry.MaxRetries && RetryPolicy.IsTransient(ex))
            {
                Interlocked.Add(ref _inFlight, -got);
                LogPieceRetry(_id, work.Track, attempt, ex.Message);
                await Task.Delay(retry.DelayFor(attempt, ex), ct).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Add(ref _inFlight, -got);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    /// <summary>HLS AES-128: CBC with PKCS7 padding over the whole piece (RFC 8216 §4.3.2.4).</summary>
    private async Task DecryptAsync(string path, StreamKey key, StreamManifestReader reader, StreamRequest request, CancellationToken ct)
    {
        var fetch = _keys.GetOrAdd(key.Url, url => new Lazy<Task<byte[]>>(() => reader.KeyAsync(url, request, ct)));
        byte[] secret;
        try
        {
            secret = await fetch.Value.ConfigureAwait(false);
        }
        catch
        {
            _keys.TryRemove(new KeyValuePair<Uri, Lazy<Task<byte[]>>>(key.Url, fetch)); // the next attempt asks again
            throw;
        }

        using var aes = Aes.Create();
        aes.Key = secret;
        byte[] plain;
        try
        {
            plain = aes.DecryptCbc(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false), key.Iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException ex)
        {
            throw new DownloadFailedException(ErrorCodes.SiteError, $"A piece of the stream could not be decrypted with its key: {ex.Message}");
        }

        await File.WriteAllBytesAsync(path, plain, ct).ConfigureAwait(false);
    }

    /// <summary>The track's pieces end to end: an initialization section and its fragments, or MPEG-TS pieces, each a valid file when joined.</summary>
    private string Join(StreamTrack track, List<Work> pieces)
    {
        var ext = track.Ext == "webm" ? "webm" : pieces.Any(p => p.Piece.IsInit) || pieces.Count == 1 ? "mp4" : "ts";
        var joined = Path.Combine(_item.MediaPartsFolder, $"{track.Id}.{ext}");
        using (var output = new FileStream(joined, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
        {
            foreach (var piece in pieces)
            {
                using var input = new FileStream(piece.Path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
                input.CopyTo(output);
            }
        }

        return joined;
    }

    /// <summary>ffmpeg copies picture and sound into the chosen container (<c>-c copy</c>: nothing is re-encoded).</summary>
    private async Task<string> MergeAsync(List<string> inputs, CancellationToken ct)
    {
        var container = _item.MediaContainer is { Length: > 0 } c ? c : "mp4";
        var output = Path.Combine(_item.MediaPartsFolder, "joined." + container);
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        foreach (var input in inputs)
        {
            args.Add("-i");
            args.Add(input);
        }

        if (inputs.Count == 1)
        {
            // Picture and sound in one track, or sound alone; nothing else (MPEG-TS can carry timed metadata MP4 refuses).
            args.AddRange(["-map", "0:v?", "-map", "0:a?"]);
        }
        else
        {
            args.AddRange(["-map", "0:v:0", "-map", "1:a:0"]);
        }

        args.AddRange(["-c", "copy", output]);
        var errors = new Queue<string>();
        var code = await _tools!.Runner.RunAsync(_tools.FfmpegPath, args, _ => { }, line =>
        {
            lock (errors)
            {
                errors.Enqueue(line);
                while (errors.Count > KeptErrorLines)
                {
                    errors.Dequeue();
                }
            }
        }, ct).ConfigureAwait(false);

        if (code != 0 || !File.Exists(output))
        {
            string? reason;
            lock (errors)
            {
                reason = errors.LastOrDefault(l => l.Trim().Length > 0);
            }

            throw new DownloadFailedException(ErrorCodes.FileError, $"ffmpeg could not put the stream's picture and sound together{(reason is null ? "." : ": " + reason.Trim())}");
        }

        return output;
    }

    private void Complete(string output)
    {
        var target = File.Exists(_item.DestPath) ? UniquePath.Allocate(_item.DestPath) : _item.DestPath;
        SafePath.EnsureInside(_item.SaveFolder, target);
        File.Move(output, target);
        var size = new FileInfo(target).Length;
        _item.DestPath = target;
        _item.FileName = Path.GetFileName(target);
        _item.TotalBytes = size;
        _item.DownloadedBytes = size;
        _item.CompletedAt = _clock.UtcNow;
        MarkOfTheWeb.TryApply(target, _item.Url, _item.Referrer);
        TryDeleteFolder(_item.MediaPartsFolder);
        SetStatus(DownloadStatus.Completed);
        _repository.AddHistoryEvent(_id, "completed", target);
        LogCompleted(_id, size);
    }

    /// <summary>Bytes so far, and the total: each track's average piece times its pieces, once every track has one; until then the size the format list estimated.</summary>
    private (long Downloaded, long? Total) Totals()
    {
        var downloaded = _progress.Values.Sum(p => p.Bytes) + Interlocked.Read(ref _inFlight);
        long? total = _progress.Count > 0 && _progress.Values.All(p => p.Done > 0)
            ? _progress.Values.Sum(p => p.Bytes * p.Count / p.Done)
            : _estimatedTotal;
        return (downloaded, total is { } t ? Math.Max(t, downloaded) : null);
    }

    private async Task CheckpointLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long downloaded;
                lock (_progressLock)
                {
                    downloaded = Totals().Downloaded;
                }

                _speed.Sample(downloaded);
                if (++ticks % 5 == 0)
                {
                    Checkpoint();
                }
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

    private sealed record Work(string Track, StreamPiece Piece, string Path);

    private sealed class TrackProgress(int count)
    {
        public int Count { get; } = count;

        public int Done { get; set; }

        public long Bytes { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream download {Id} started: {Url}, tracks {Format}, {Pieces} pieces, resume={Resume}")]
    private partial void LogStarted(long id, string url, string format, int pieces, bool resume);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream download {Id}: a piece of track {Track} failed (attempt {Attempt}), retrying: {Reason}")]
    private partial void LogPieceRetry(long id, string track, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream download {Id} paused at {Bytes} bytes")]
    private partial void LogPaused(long id, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream download {Id} completed, {Bytes} bytes")]
    private partial void LogCompleted(long id, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream download {Id} failed with {Code}")]
    private partial void LogFailed(Exception ex, long id, string code);
}
