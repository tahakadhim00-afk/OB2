using System.Net;
using System.Net.Http.Headers;
using OB2.Core.Downloads;

namespace OB2.Core.Http;

/// <summary>
/// Creates the shared <see cref="HttpClient"/> and the requests the engine sends (FR-8).
/// Cookies are sent as a raw header instead of a cookie container so each download carries
/// exactly the cookies the browser handed over.
/// </summary>
public static class DownloadHttpClient
{
    private static readonly HashSet<string> RestrictedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Range", "If-Range", "Content-Length", "Transfer-Encoding", "Connection",
        "Accept-Encoding", "Cookie", "Referer", "User-Agent", "Expect", "Upgrade", "TE", "Keep-Alive",
        "Proxy-Authorization", "Proxy-Connection",
    };

    /// <summary>Headers that may carry credentials; never persisted or logged (§11.1).</summary>
    public static readonly IReadOnlySet<string> SensitiveHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Cookie", "Authorization", "Proxy-Authorization",
    };

    public static HttpClient Create(TimeSpan connectTimeout, int maxConnectionsPerServer = 64)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = connectTimeout,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            // Byte ranges must address the raw entity, so never let the handler decompress.
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            MaxConnectionsPerServer = maxConnectionsPerServer,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            // Null proxy with UseProxy = system proxy settings (FR-10 manual proxy is backlog).
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            // Per-read idle timeouts are applied by the engine (§11.3).
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public static HttpRequestMessage CreateRequest(HttpMethod method, Uri url, RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = new HttpRequestMessage(method, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };

        var headers = request.Headers;
        headers.TryAddWithoutValidation("User-Agent", context.UserAgent);
        headers.TryAddWithoutValidation("Accept", "*/*");
        headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        if (!string.IsNullOrEmpty(context.Referrer) && Uri.TryCreate(context.Referrer, UriKind.Absolute, out var referrer))
        {
            headers.Referrer = referrer;
        }

        if (!string.IsNullOrEmpty(context.Cookies))
        {
            headers.TryAddWithoutValidation("Cookie", context.Cookies);
        }

        foreach (var (name, value) in context.Headers)
        {
            if (!RestrictedHeaders.Contains(name))
            {
                headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    /// <summary>Keeps only headers that are safe to store in <c>headers_json</c>.</summary>
    public static Dictionary<string, string> PersistableHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result;
        }

        foreach (var (name, value) in headers)
        {
            if (!SensitiveHeaders.Contains(name) && !RestrictedHeaders.Contains(name))
            {
                result[name] = value;
            }
        }

        return result;
    }
}

/// <summary>Everything that identifies the requester for one download.</summary>
public sealed record RequestContext(string UserAgent, string? Referrer, string? Cookies, IReadOnlyDictionary<string, string> Headers)
{
    public static RequestContext For(DownloadItem item, string defaultUserAgent, string? cookies) =>
        new(item.UserAgent ?? defaultUserAgent, item.Referrer, cookies, item.Headers);
}
