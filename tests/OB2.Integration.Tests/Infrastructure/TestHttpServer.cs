using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OB2.Integration.Tests.Infrastructure;

/// <summary>
/// Local HTTP server for engine tests (PRD §14.1). Serves deterministic content whose
/// bytes depend on their offset, so any misplaced range changes the checksum.
///
/// <c>/file/{size}/{name}?options</c>:
/// <list type="bullet">
/// <item><c>norange=1</c> ignore Range, no Accept-Ranges</item>
/// <item><c>nolength=1</c> no Content-Length (chunked), no ranges</item>
/// <item><c>bps=N</c> throttle each response to N bytes/s</item>
/// <item><c>dropAfter=N&amp;drops=K</c> abort the first K responses after N bytes</item>
/// <item><c>fail=CODE&amp;failCount=K</c> answer CODE to the first K GETs</item>
/// <item><c>stallAfter=N&amp;stalls=K</c> stop sending (but keep the socket) after N bytes, K times</item>
/// <item><c>headStatus=CODE</c> answer HEAD with CODE</item>
/// <item><c>cookie=a=b</c> require that cookie, else 403</item>
/// <item><c>disposition=NAME</c> send Content-Disposition: attachment</item>
/// <item><c>etag=X</c> override the ETag</item>
/// </list>
/// <c>/redirect?to=/path</c> answers 302. <c>/status/{code}</c> answers that code.
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private const int Period = 65_521;
    private static readonly byte[] Pattern = CreatePattern();

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, int> _counters = new();

    private TestHttpServer(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Connections currently streaming a body.</summary>
    public int ActiveResponses => Volatile.Read(ref _activeResponses);

    public int MaxConcurrentResponses => Volatile.Read(ref _maxConcurrentResponses);

    private int _activeResponses;
    private int _maxConcurrentResponses;

    public static async Task<TestHttpServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, 0);
            o.Limits.MinResponseDataRate = null;
            o.Limits.MaxConcurrentConnections = null;
        });

        var app = builder.Build();
        TestHttpServer? server = null;
        app.Run(context => server!.HandleAsync(context));
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new TestHttpServer(app, new Uri(address));
        return server;
    }

    public Uri Url(string pathAndQuery) => new(BaseUri, pathAndQuery);

    public Uri FileUrl(long size, string name = "file.bin", string query = "") =>
        Url($"/file/{size.ToString(CultureInfo.InvariantCulture)}/{Uri.EscapeDataString(name)}{(query.Length == 0 ? string.Empty : "?" + query)}");

    public static byte ByteAt(long offset) =>
        (byte)(Pattern[offset % Period] ^ (byte)(offset / Period) ^ (byte)(offset / Period >> 8));

    public static void Fill(Span<byte> destination, long offset)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = ByteAt(offset + i);
        }
    }

    public static string Sha256Of(long size)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        for (long offset = 0; offset < size; offset += buffer.Length)
        {
            var n = (int)Math.Min(buffer.Length, size - offset);
            Fill(buffer.AsSpan(0, n), offset);
            hash.AppendData(buffer, 0, n);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string Md5Of(long size)
    {
        var data = new byte[size];
        Fill(data, 0);
        return Convert.ToHexString(MD5.HashData(data));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        Requests.Enqueue(new RecordedRequest(
            request.Method,
            request.Path + request.QueryString,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));

        var segments = request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (segments.FirstOrDefault())
        {
            case "file" when segments.Length >= 2 && long.TryParse(segments[1], CultureInfo.InvariantCulture, out var size):
                await ServeFileAsync(context, size);
                break;
            case "redirect":
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = request.Query["to"].ToString();
                break;
            case "status" when segments.Length >= 2:
                context.Response.StatusCode = int.Parse(segments[1], CultureInfo.InvariantCulture);
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    private async Task ServeFileAsync(HttpContext context, long size)
    {
        var request = context.Request;
        var response = context.Response;
        var query = request.Query;
        var key = request.Path + request.QueryString;
        var isHead = HttpMethods.IsHead(request.Method);
        var noRange = query.ContainsKey("norange") || query.ContainsKey("nolength");
        var noLength = query.ContainsKey("nolength");

        if (query.TryGetValue("cookie", out var requiredCookie)
            && !request.Headers.Cookie.ToString().Contains(requiredCookie.ToString(), StringComparison.Ordinal))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (isHead && query.TryGetValue("headStatus", out var headStatus))
        {
            response.StatusCode = int.Parse(headStatus!, CultureInfo.InvariantCulture);
            return;
        }

        if (!isHead && query.TryGetValue("fail", out var failCode)
            && Increment(key + "#fail") <= Int(query, "failCount", int.MaxValue))
        {
            response.StatusCode = int.Parse(failCode!, CultureInfo.InvariantCulture);
            return;
        }

        if (query.TryGetValue("disposition", out var disposition))
        {
            // Header values must be ASCII: plain filename gets a fallback, filename* carries Unicode (RFC 6266).
            var ascii = new string(disposition.ToString().Select(c => c < 128 && c != '"' ? c : '_').ToArray());
            response.Headers.ContentDisposition = $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(disposition!)}";
        }

        response.Headers.ETag = query.TryGetValue("etag", out var etag) ? $"\"{etag}\"" : $"\"v1-{size}\"";
        response.Headers.LastModified = "Wed, 01 Jan 2025 00:00:00 GMT";
        response.ContentType = "application/octet-stream";

        long from = 0, to = size - 1;
        if (!noRange)
        {
            response.Headers.AcceptRanges = "bytes";
            if (TryParseRange(request.Headers.Range.ToString(), size, out var rangeFrom, out var rangeTo))
            {
                if (rangeFrom >= size)
                {
                    response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    response.Headers.ContentRange = $"bytes */{size}";
                    return;
                }

                from = rangeFrom;
                to = rangeTo;
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {from}-{to}/{size}";
            }
        }

        if (!noLength)
        {
            response.ContentLength = to - from + 1;
        }

        if (isHead)
        {
            return;
        }

        var bps = Int(query, "bps", 0);
        var dropAfter = query.ContainsKey("dropAfter") && Increment(key + "#drop") <= Int(query, "drops", int.MaxValue)
            ? Int(query, "dropAfter", 0)
            : -1;
        var stallAfter = query.ContainsKey("stallAfter") && Increment(key + "#stall") <= Int(query, "stalls", int.MaxValue)
            ? Int(query, "stallAfter", 0)
            : -1;

        var active = Interlocked.Increment(ref _activeResponses);
        InterlockedMax(ref _maxConcurrentResponses, active);
        try
        {
            await WriteBodyAsync(context, from, to, bps, dropAfter, stallAfter);
        }
        finally
        {
            Interlocked.Decrement(ref _activeResponses);
        }
    }

    private static async Task WriteBodyAsync(HttpContext context, long from, long to, int bps, long dropAfter, long stallAfter)
    {
        var chunk = bps > 0 ? Math.Clamp(bps / 10, 1024, 64 * 1024) : 64 * 1024;
        var buffer = new byte[chunk];
        long sent = 0;
        var started = DateTime.UtcNow;
        var ct = context.RequestAborted;

        for (var offset = from; offset <= to;)
        {
            var n = (int)Math.Min(chunk, to - offset + 1);
            if (dropAfter >= 0 && sent + n > dropAfter)
            {
                n = (int)(dropAfter - sent);
            }

            if (stallAfter >= 0 && sent + n > stallAfter)
            {
                n = (int)(stallAfter - sent);
            }

            if (n > 0)
            {
                Fill(buffer.AsSpan(0, n), offset);
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
                await context.Response.Body.FlushAsync(ct);
                offset += n;
                sent += n;
            }

            if (dropAfter >= 0 && sent >= dropAfter)
            {
                context.Abort();
                return;
            }

            if (stallAfter >= 0 && sent >= stallAfter)
            {
                await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
                return;
            }

            if (bps > 0)
            {
                var due = started + TimeSpan.FromSeconds((double)sent / bps);
                var wait = due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }
            }
        }
    }

    private static bool TryParseRange(string header, long size, out long from, out long to)
    {
        from = 0;
        to = size - 1;
        if (!header.StartsWith("bytes=", StringComparison.Ordinal) || header.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = header["bytes=".Length..].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out from))
        {
            return false;
        }

        if (parts[1].Length > 0)
        {
            to = Math.Min(long.Parse(parts[1], CultureInfo.InvariantCulture), size - 1);
        }

        return true;
    }

    private int Increment(string key) => _counters.AddOrUpdate(key, 1, (_, n) => n + 1);

    private static int Int(IQueryCollection query, string name, int fallback) =>
        query.TryGetValue(name, out var value) ? int.Parse(value!, CultureInfo.InvariantCulture) : fallback;

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static byte[] CreatePattern()
    {
        var pattern = new byte[Period];
        new Random(20261010).NextBytes(pattern);
        return pattern;
    }
}

public sealed record RecordedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}
