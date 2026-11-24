using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using OB2.Core.Http;
using OB2.Core.Organization;

namespace OB2.Core.Grabber;

/// <summary>
/// D-87: the file links in a web page, when a link pasted into Grab links turns out to be a page
/// (an article, a download page). The same idea as the extension's "Find downloads on this page"
/// (D-70), on the page's HTML as the server sends it: links (<c>a</c>, <c>area</c>; a
/// <c>download</c> attribute makes any link a file), media elements (<c>video</c>, <c>audio</c>,
/// <c>source</c>, <c>embed</c>, <c>object</c>), the media meta tags, and addresses written in
/// scripts. Relative addresses resolve against the page (or its <c>base</c>). What a script builds
/// at run time is not in the HTML, so it is not found here; the browser's button finds it.
/// </summary>
public static partial class PageLinks
{
    private static readonly string[] MediaMeta =
        ["og:video", "og:video:url", "og:video:secure_url", "og:audio", "og:audio:url", "og:audio:secure_url", "twitter:player:stream"];

    /// <summary>A start tag; a <c>&gt;</c> inside a quoted value (<c>title="a &gt; b"</c>) does not end it.</summary>
    [GeneratedRegex(@"<(a|area|video|audio|source|embed|object|meta|base)\b((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"([a-zA-Z_:][-a-zA-Z0-9_:.]*)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'=<>`]+)))?", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    /// <param name="html">The page as the server sent it.</param>
    /// <param name="pageUrl">Where it came from, after redirects: relative links resolve against it.</param>
    /// <param name="fileExtensions">More types that count as files: the capture rules' (Options > Browser), so a type the user added is found too.</param>
    public static IReadOnlyList<Uri> Find(string html, Uri pageUrl, IReadOnlyCollection<string>? fileExtensions = null, int max = LinkExtractor.MaxLinks)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUrl);
        var extra = new HashSet<string>((fileExtensions ?? []).Select(e => e.Trim().TrimStart('.').ToLowerInvariant()), StringComparer.Ordinal);
        var page = pageUrl.GetLeftPart(UriPartial.Query);
        var baseUrl = pageUrl;
        var seen = new HashSet<string>(StringComparer.Ordinal) { page };
        var found = new List<Uri>();

        void Add(string? raw, Uri relativeTo, Accept accept)
        {
            if (found.Count >= max || string.IsNullOrWhiteSpace(raw)
                || !Uri.TryCreate(relativeTo, WebUtility.HtmlDecode(raw.Trim()), out var url)
                || UrlValidator.Check(url.AbsoluteUri, out _) is not (UrlCheck.Ok or UrlCheck.HasCredentials))
            {
                return; // javascript:, mailto:, data:, blob:, "#top", or not an address at all
            }

            var link = url.Fragment.Length == 0 ? url : new Uri(url.GetLeftPart(UriPartial.Query));
            if (IsFile(link, accept, extra) && seen.Add(link.AbsoluteUri))
            {
                found.Add(link);
            }
        }

        foreach (Match tag in Tag().Matches(html))
        {
            var name = tag.Groups[1].Value.ToLowerInvariant();
            var attributes = Attributes(tag.Groups[2].Value);
            switch (name)
            {
                case "base" when baseUrl == pageUrl && attributes.TryGetValue("href", out var href)
                                 && Uri.TryCreate(pageUrl, WebUtility.HtmlDecode(href), out var declared) && declared.Scheme is "http" or "https":
                    baseUrl = declared;
                    break;
                case "a" or "area":
                    // <a download> is a file whatever its address looks like (download.php?id=7).
                    Add(attributes.GetValueOrDefault("href"), baseUrl, attributes.ContainsKey("download") ? Accept.Any : Accept.Link);
                    break;
                case "video" or "audio" or "source" or "embed":
                    Add(attributes.GetValueOrDefault("src"), baseUrl, name == "embed" ? Accept.Link : Accept.Any);
                    break;
                case "object":
                    Add(attributes.GetValueOrDefault("data"), baseUrl, Accept.Link);
                    break;
                case "meta" when MediaMeta.Contains((attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name") ?? string.Empty).ToLowerInvariant()):
                    Add(attributes.GetValueOrDefault("content"), baseUrl, Accept.Any);
                    break;
            }
        }

        // Addresses in scripts and JSON (players keep the file's address there). Only full
        // addresses, and only media and the capture types: a script also names its thumbnails,
        // icons and the site's own pages.
        foreach (var url in LinkExtractor.Extract(html))
        {
            Add(url.AbsoluteUri, pageUrl, Accept.Text);
        }

        return found;
    }

    private enum Accept
    {
        /// <summary>A media element or a link marked <c>download</c>: whatever its address.</summary>
        Any,

        /// <summary>A link: any type OB2 sorts into a category (pictures too), or a capture type.</summary>
        Link,

        /// <summary>An address in the page's text: video, music, or a capture type.</summary>
        Text,
    }

    private static bool IsFile(Uri url, Accept accept, HashSet<string> extra)
    {
        if (accept == Accept.Any)
        {
            return true;
        }

        var name = url.Segments[^1];
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0)
        {
            return false;
        }

        if (extra.Contains(extension))
        {
            return true;
        }

        var kind = CategoryKinds.OfFile(name);
        return accept == Accept.Link
            ? kind != CategoryKind.Other
            // "ts" is also a stream's piece and TypeScript's source; neither is a download.
            : kind is CategoryKind.Video or CategoryKind.Music && extension != "ts";
    }

    private static Dictionary<string, string> Attributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Attribute().Matches(text))
        {
            var value = match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Success ? match.Groups[3].Value
                : match.Groups[4].Value; // a bare attribute (download) has an empty value
            attributes.TryAdd(match.Groups[1].Value, value);
        }

        return attributes;
    }
}

/// <summary>D-87: fetches a web page's HTML for <see cref="PageLinks"/>, with the engine's client and user agent.</summary>
public sealed class PageReader
{
    /// <summary>Larger pages are read up to here: a page's links come long before 5 MB.</summary>
    public const int MaxPageBytes = 5 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly TimeSpan _timeout;

    public PageReader(HttpClient http, string userAgent, TimeSpan timeout)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _userAgent = userAgent ?? throw new ArgumentNullException(nameof(userAgent));
        _timeout = timeout;
    }

    /// <returns>The page's text and its address after redirects.</returns>
    /// <exception cref="HttpStatusException">The server refused the page.</exception>
    /// <exception cref="TimeoutException">The server did not answer in time.</exception>
    public async Task<(string Html, Uri Url)> ReadAsync(Uri url, Uri? referrer, CancellationToken cancellationToken = default)
    {
        UrlValidator.EnsureDownloadable(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var request = DownloadHttpClient.CreateRequest(HttpMethod.Get, url, new RequestContext(_userAgent, referrer?.AbsoluteUri, null, new Dictionary<string, string>()));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw HttpStatusException.From(response);
            }

            var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var page = new MemoryStream();
                var chunk = new byte[64 * 1024];
                int read;
                while (page.Length < MaxPageBytes
                       && (read = await body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaxPageBytes - page.Length)), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    page.Write(chunk, 0, read);
                }

                var text = EncodingOf(response.Content.Headers.ContentType).GetString(page.GetBuffer(), 0, (int)page.Length);
                return (text, response.RequestMessage?.RequestUri ?? url);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The server did not answer within {_timeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>The page's declared charset; addresses are ASCII, so UTF-8 reads them even when it is a stranger one.</summary>
    private static Encoding EncodingOf(MediaTypeHeaderValue? contentType)
    {
        try
        {
            return contentType?.CharSet is { Length: > 0 } charset ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
