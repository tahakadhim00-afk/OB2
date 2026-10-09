using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using OB2.Core.Downloads;
using OB2.Core.Http;

namespace OB2.Core.Engine;

/// <summary>
/// Streams one segment's byte range into the partial file at its offset. One attempt per
/// call; retry decisions belong to the caller.
/// </summary>
internal sealed class SegmentWorker
{
    private readonly HttpClient _http;
    private readonly EngineOptions _options;
    private readonly SafeFileHandle _file;
    private readonly IReadOnlyList<TokenBucket> _limiters;
    private readonly Action _onBytes;

    public SegmentWorker(HttpClient http, EngineOptions options, SafeFileHandle file, IReadOnlyList<TokenBucket> limiters, Action onBytes)
    {
        _http = http;
        _options = options;
        _file = file;
        _limiters = limiters;
        _onBytes = onBytes;
    }

    /// <summary>Downloads the rest of <paramref name="segment"/>. Returns bytes received in this attempt.</summary>
    public async Task<long> RunAsync(Uri url, RequestContext context, Segment segment, bool useRanges, CancellationToken cancellationToken)
    {
        if (!useRanges && segment.Downloaded > 0)
        {
            // Without range support the only way to continue is from the start.
            segment.Downloaded = 0;
        }

        var position = segment.Position;
        using var request = DownloadHttpClient.CreateRequest(HttpMethod.Get, url, context);
        if (useRanges)
        {
            request.Headers.Range = segment.IsOpenEnded
                ? new RangeHeaderValue(position, null)
                : new RangeHeaderValue(position, segment.End);
        }

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_options.ReadIdleTimeout);

        long received = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            Validate(response, position);

            await using var body = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            while (true)
            {
                var chunk = ChunkSize();
                var want = segment.IsOpenEnded ? chunk : (int)Math.Min(chunk, segment.Length!.Value - segment.Downloaded);
                if (want <= 0)
                {
                    break;
                }

                idle.CancelAfter(_options.ReadIdleTimeout);
                var read = await body.ReadAsync(buffer.AsMemory(0, want), idle.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    if (segment.IsOpenEnded)
                    {
                        break;
                    }

                    throw new IOException($"The connection closed after {received:N0} bytes, {segment.Length - segment.Downloaded:N0} short of the segment end.");
                }

                await WriteAsync(buffer.AsMemory(0, read), segment.Position, cancellationToken).ConfigureAwait(false);
                segment.AddDownloaded(read);
                received += read;
                _onBytes();

                foreach (var limiter in _limiters)
                {
                    await limiter.WaitAsync(read, cancellationToken).ConfigureAwait(false);
                }
            }

            segment.Status = SegmentStatus.Done;
            if (segment.IsOpenEnded)
            {
                // Size is now known; pin the range so the segment reads as complete.
                segment.End = segment.Start + segment.Downloaded - 1;
            }

            return received;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No data received for {_options.ReadIdleTimeout.TotalSeconds:0} seconds.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private int ChunkSize()
    {
        var chunk = _options.BufferSize;
        foreach (var limiter in _limiters)
        {
            chunk = limiter.SuggestedChunk(chunk);
        }

        return chunk;
    }

    /// <summary>Disk errors are never retried, so they are wrapped to keep them apart from network IOExceptions.</summary>
    private async Task WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken cancellationToken)
    {
        try
        {
            await RandomAccess.WriteAsync(_file, data, offset, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            var code = ErrorCodes.From(ex) == ErrorCodes.DiskFull ? ErrorCodes.DiskFull : ErrorCodes.FileError;
            throw new DownloadFailedException(code, $"Could not write the partial file: {ex.Message}");
        }
    }

    private static void Validate(HttpResponseMessage response, long position)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.PartialContent:
                var range = response.Content.Headers.ContentRange;
                if (range?.From != position)
                {
                    throw new RangeNotSupportedException($"Asked for bytes from {position} but the server sent range '{range}'.");
                }

                return;

            case HttpStatusCode.OK:
                // A full response is usable only when we wanted the file from its first byte.
                if (position != 0)
                {
                    throw new RangeNotSupportedException("The server ignored the Range header and sent the whole file.");
                }

                return;

            default:
                throw HttpStatusException.From(response);
        }
    }
}
