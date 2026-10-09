using System.Net;
using System.Net.Http.Headers;
using OB2.Core.Files;

namespace OB2.Core.Http;

/// <summary>What the server told us about a URL before downloading (FR-2).</summary>
public sealed record ProbeResult(
    Uri FinalUrl,
    long? TotalBytes,
    bool SupportsRanges,
    string SuggestedFileName,
    string? ETag,
    string? LastModified,
    string? ContentType);

/// <summary>
/// Probes a URL with HEAD, falling back to a one-byte ranged GET when HEAD is refused or
/// does not prove range support (FR-2). Redirects are followed; the final URL is reported.
/// </summary>
public sealed class UrlProber
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public UrlProber(HttpClient http, TimeSpan timeout)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout;
    }

    public async Task<ProbeResult> ProbeAsync(Uri url, RequestContext context, string? fileNameHint, CancellationToken cancellationToken)
    {
        UrlValidator.EnsureDownloadable(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var ct = timeout.Token;

        try
        {
            ProbeResult? head = null;
            try
            {
                head = await HeadAsync(url, context, fileNameHint, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested)
            {
                // Some servers drop HEAD entirely; the GET below decides.
            }

            if (head is { SupportsRanges: true, TotalBytes: not null })
            {
                return head;
            }

            try
            {
                return await RangedGetAsync(url, context, fileNameHint, head, ct).ConfigureAwait(false);
            }
            catch (HttpStatusException) when (head is not null)
            {
                return head;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The server did not answer within {_timeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<ProbeResult?> HeadAsync(Uri url, RequestContext context, string? hint, CancellationToken ct)
    {
        using var request = DownloadHttpClient.CreateRequest(HttpMethod.Head, url, context);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var acceptRanges = response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);
        return Build(url, response, response.Content.Headers.ContentLength, acceptRanges, hint);
    }

    private async Task<ProbeResult> RangedGetAsync(Uri url, RequestContext context, string? hint, ProbeResult? head, CancellationToken ct)
    {
        using var request = DownloadHttpClient.CreateRequest(HttpMethod.Get, url, context);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        // Headers only: disposing the response abandons the body.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        switch (response.StatusCode)
        {
            case HttpStatusCode.PartialContent:
                var range = response.Content.Headers.ContentRange;
                return Build(url, response, range?.Length ?? head?.TotalBytes, supportsRanges: range?.Length is not null, hint);
            case HttpStatusCode.OK:
                return Build(url, response, response.Content.Headers.ContentLength, supportsRanges: false, hint);
            default:
                throw new HttpStatusException(response.StatusCode, response.ReasonPhrase);
        }
    }

    private static ProbeResult Build(Uri url, HttpResponseMessage response, long? total, bool supportsRanges, string? hint)
    {
        var finalUrl = response.RequestMessage?.RequestUri ?? url;
        var headers = response.Content.Headers;
        var name = FileNameResolver.Resolve(headers.ContentDisposition, hint, finalUrl, url);
        return new ProbeResult(
            finalUrl,
            total,
            supportsRanges,
            name,
            response.Headers.ETag?.ToString(),
            headers.LastModified?.ToString("R"),
            headers.ContentType?.MediaType);
    }
}

/// <summary>A non-success HTTP status from the server.</summary>
public sealed class HttpStatusException : Exception
{
    public HttpStatusException(HttpStatusCode statusCode, string? reason, TimeSpan? retryAfter = null)
        : base($"The server answered {(int)statusCode} {reason}.")
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>Builds the exception from a response, reading <c>Retry-After</c> if present.</summary>
    public static HttpStatusException From(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var header = response.Headers.RetryAfter;
        TimeSpan? retryAfter = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (retryAfter < TimeSpan.Zero)
        {
            retryAfter = TimeSpan.Zero;
        }

        return new HttpStatusException(response.StatusCode, response.ReasonPhrase, retryAfter);
    }

    public TimeSpan? RetryAfter { get; }

    public HttpStatusException()
    {
    }

    public HttpStatusException(string message)
        : base(message)
    {
    }

    public HttpStatusException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>408, 425, 429, and 5xx are worth retrying; other errors will not change.</summary>
    public bool IsTransient =>
        StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or (HttpStatusCode)425
        || (int)StatusCode >= 500;
}
