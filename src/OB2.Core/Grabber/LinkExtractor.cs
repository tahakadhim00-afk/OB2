using System.Text.RegularExpressions;
using OB2.Core.Http;

namespace OB2.Core.Grabber;

/// <summary>
/// D-87: finds the http(s) addresses in any text the user pastes into Grab links: an email, a
/// forum post, a page's text or source, a list of addresses. Each address is listed once, in the
/// order it first appears, without its <c>#…</c> part (which never reaches the server).
/// </summary>
public static partial class LinkExtractor
{
    /// <summary>How many links one window holds; a pasted page rarely has more than a few hundred.</summary>
    public const int MaxLinks = 1000;

    /// <summary>
    /// Punctuation that ends a sentence or a clause rather than an address, also in Arabic and
    /// CJK text, and Markdown's <c>**bold**</c>. <c>=</c>, <c>&amp;</c>, <c>-</c> and <c>_</c> stay:
    /// addresses do end in them (a base64 token ends in <c>=</c>). <c>)</c> is handled separately:
    /// it belongs to the address when the address opened it (Wikipedia's <c>File_(1).pdf</c>).
    /// </summary>
    private const string TrailingPunctuation = ".,;:!?*،؛؟。，、；：！？…";

    /// <summary>
    /// An address runs until a character that cannot appear in one written in text: white space,
    /// quotes, angle brackets (HTML, &lt;https://…&gt; in email), backslash and the characters RFC 3986
    /// never allows unescaped. Square brackets end it too, so Markdown's <c>[https://a](https://a)</c>
    /// gives the address once; an IPv6 host (<c>http://[::1]/</c>) is not found as a result.
    /// </summary>
    [GeneratedRegex(@"https?://[^\s""'<>`{}|\\^\[\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Candidate();

    /// <param name="max">At most this many links are returned; the rest are counted in <paramref name="leftOut"/>.</param>
    public static IReadOnlyList<Uri> Extract(string? text, out int leftOut, int max = MaxLinks)
    {
        leftOut = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<Uri>();
        foreach (var link in Addresses(text))
        {
            if (!seen.Add(link.AbsoluteUri))
            {
                continue;
            }

            if (links.Count < max)
            {
                links.Add(link);
            }
            else
            {
                leftOut++;
            }
        }

        return links;
    }

    public static IReadOnlyList<Uri> Extract(string? text) => Extract(text, out _);

    /// <summary>True if the text holds at least one address; stops at the first.</summary>
    public static bool ContainsLink(string? text) => Addresses(text).Any();

    /// <summary>Every valid address in the text, repeats included.</summary>
    private static IEnumerable<Uri> Addresses(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (Match match in Candidate().Matches(Unescape(text)))
        {
            // Skipped: "http://" alone, a bad host, or longer than 2048 characters.
            if (UrlValidator.Check(TrimEnd(match.Value), out var url) is UrlCheck.Ok or UrlCheck.HasCredentials)
            {
                yield return WithoutFragment(url!);
            }
        }
    }

    /// <summary>
    /// The JSON and HTML spellings of <c>/</c> and <c>&amp;</c>, as in a page's source or a
    /// player's settings: <c>https:\/\/cdn\/a.mp4?x=1&amp;amp;y=2</c>. The same as the extension's
    /// page scan (D-70).
    /// </summary>
    private static string Unescape(string text) => text
        .Replace("\\/", "/", StringComparison.Ordinal)
        .Replace("\\u002f", "/", StringComparison.OrdinalIgnoreCase)
        .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
        .Replace("&amp;", "&", StringComparison.Ordinal);

    /// <summary>Drops sentence punctuation and closing brackets the address did not open.</summary>
    private static string TrimEnd(string candidate)
    {
        var end = candidate.Length;
        while (end > 0)
        {
            var last = candidate[end - 1];
            if (TrailingPunctuation.Contains(last, StringComparison.Ordinal)
                || (last == ')' && Count(candidate, end, '(') < Count(candidate, end, ')')))
            {
                end--;
            }
            else
            {
                break;
            }
        }

        return candidate[..end];
    }

    private static int Count(string text, int length, char c)
    {
        var count = 0;
        for (var i = 0; i < length; i++)
        {
            if (text[i] == c)
            {
                count++;
            }
        }

        return count;
    }

    private static Uri WithoutFragment(Uri url) =>
        url.Fragment.Length == 0 ? url : new Uri(url.GetLeftPart(UriPartial.Query));
}
