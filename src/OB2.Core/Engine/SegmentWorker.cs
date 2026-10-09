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

    public SegmentWorker(HttpClient http, EngineOptions options, SafeFileHandle file)
    {
        _http = http;
        _options = options;
        _file = file;
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
            Validate(response, segment, position, useRanges);

            await using var body = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            while (true)
            {
                var want = segment.IsOpenEnded
                    ? _options.BufferSize
                    : (int)Math.Min(_options.BufferSize, segment.Length!.Value - segment.Downloaded);
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

                await RandomAccess.WriteAsync(_file, buffer.AsMemory(0, read), segment.Position, cancellationToken).ConfigureAwait(false);
                segment.AddDownloaded(read);
                received += read;
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

    private static void Validate(HttpResponseMessage response, Segment segment, long position, bool useRanges)
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
                // A full response is fine only when we wanted the file from its first byte.
                if (position != 0)
                {
                    throw new RangeNotSupportedException("The server ignored the Range header and sent the whole file.");
                }

                return;

            default:
                throw new HttpStatusException(response.StatusCode, response.ReasonPhrase);
        }
    }
}
