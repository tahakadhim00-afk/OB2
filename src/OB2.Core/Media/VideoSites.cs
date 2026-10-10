using System.Text.RegularExpressions;

namespace OB2.Core.Media;

/// <summary>
/// Pages that always go to yt-dlp (D-71) wherever the address comes from: the Add dialog, the
/// command line, or a browser link. Other video sites arrive marked by the extension.
/// </summary>
public static partial class VideoSites
{
    private static readonly HashSet<string> YouTubeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtube-nocookie.com", "www.youtube-nocookie.com",
    };

    /// <summary>
    /// The address of one YouTube video (watch, Shorts, live, embed, youtu.be) as
    /// <c>https://www.youtube.com/watch?v=ID</c>, without playlist or time parameters; else null.
    /// </summary>
    public static Uri? YouTubeVideo(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var segments = url.AbsolutePath.Trim('/').Split('/');
        string? id = null;
        if (string.Equals(url.Host, "youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            id = segments[0];
        }
        else if (YouTubeHosts.Contains(url.Host))
        {
            if (url.AbsolutePath == "/watch")
            {
                id = QueryValue(url, "v");
            }
            else if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed" or "v")
            {
                id = segments[1];
            }
        }

        if (id is null || !VideoId().IsMatch(id))
        {
            return null;
        }

        var host = string.Equals(url.Host, "music.youtube.com", StringComparison.OrdinalIgnoreCase) ? "music.youtube.com" : "www.youtube.com";
        return new Uri($"https://{host}/watch?v={id}");
    }

    private static string? QueryValue(Uri url, string name)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && pair[..eq] == name)
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoId();
}
