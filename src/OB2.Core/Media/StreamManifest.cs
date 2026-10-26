using System.Text.RegularExpressions;
using OB2.Core.Http;

namespace OB2.Core.Media;

public enum StreamKind
{
    Hls,
    Dash,
}

/// <summary>
/// D-80: an HLS or DASH stream as OB2 reads it itself, without yt-dlp: its picture and sound
/// tracks and, once known, the pieces each is made of.
/// </summary>
/// <param name="Url">The manifest's address.</param>
/// <param name="Duration">The whole video's length; null until a playlist that lists its pieces was read.</param>
public sealed record StreamManifest(StreamKind Kind, Uri Url, TimeSpan? Duration, IReadOnlyList<StreamTrack> Tracks)
{
    /// <summary>
    /// The tracks as formats, so the button's menu and the Download Video window list them like
    /// yt-dlp's. Each format's id is its track's, and <see cref="MediaChoices.Select(MediaFormat?, MediaFormat?)"/>
    /// turns a pick into a <see cref="StreamSpec"/>.
    /// </summary>
    public MediaInfo ToMediaInfo()
    {
        var formats = Tracks.Select(t => new MediaFormat
        {
            Id = t.Id,
            Ext = t.Ext,
            VideoCodec = t.HasVideo ? t.VideoCodec : "none",
            AudioCodec = t.HasAudio ? t.AudioCodec : "none",
            Height = t.Height,
            Width = t.Width,
            Fps = t.Fps,
            Bitrate = t.Bandwidth / 1000.0,
            AudioBitrate = t.HasVideo ? null : t.Bandwidth / 1000.0,
            Size = Duration is { } d && t.Bandwidth is { } bps ? (long)(bps / 8.0 * d.TotalSeconds) : null,
            Note = t.Name,
            Language = t.Language,
            FromStream = true,
        }).ToList();
        var name = Path.GetFileNameWithoutExtension(Url.AbsolutePath);
        return new MediaInfo(name.Length > 0 ? name : "video", "OB2", Duration, formats) { FromStream = true };
    }
}

/// <summary>D-80: one picture quality or sound track of a stream.</summary>
public sealed record StreamTrack
{
    /// <summary><c>v0</c>, <c>v1</c>… for pictures and <c>a0</c>… for sound, in the manifest's order, which a later read repeats.</summary>
    public required string Id { get; init; }

    public bool HasVideo { get; init; }

    /// <summary>False for a picture-only track, which needs a sound track beside it.</summary>
    public bool HasAudio { get; init; }

    /// <summary><c>mp4</c> or <c>webm</c> for pictures, <c>m4a</c> or <c>webm</c> for sound.</summary>
    public required string Ext { get; init; }

    public string? VideoCodec { get; init; }

    public string? AudioCodec { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? Fps { get; init; }

    /// <summary>Bits per second, as the manifest declares it.</summary>
    public long? Bandwidth { get; init; }

    public string? Language { get; init; }

    /// <summary>The sound track's name ("English, default"); null for pictures.</summary>
    public string? Name { get; init; }

    /// <summary>HLS: the playlist that lists the pieces, read when they are needed. Null when <see cref="Pieces"/> is known.</summary>
    public Uri? PlaylistUrl { get; init; }

    /// <summary>The pieces in playing order; null while only <see cref="PlaylistUrl"/> is known.</summary>
    public IReadOnlyList<StreamPiece>? Pieces { get; init; }
}

/// <summary>
/// D-80: one piece of a track: the initialization data a player reads first
/// (<see cref="IsInit"/>), or a few seconds of the video. Joined in order they make the track's file.
/// </summary>
/// <param name="Offset">With <paramref name="Length"/>, the byte range of <paramref name="Url"/> that is this piece.</param>
/// <param name="Key">HLS AES-128: how the piece is encrypted; null when it is not.</param>
public sealed record StreamPiece(Uri Url, long? Offset, long? Length, StreamKey? Key, bool IsInit = false);

/// <summary>D-80: HLS AES-128 (the playlist hands out the key, as to any player): the key's address and the CBC IV.</summary>
public sealed record StreamKey(Uri Url, byte[] Iv);

/// <summary>
/// D-80: a format picked from <see cref="StreamManifest.ToMediaInfo"/>, as stored in
/// <c>media_format</c>: <c>stream:v1+a0</c> (picture and sound), <c>stream:v2</c> (a picture
/// with its own sound) or <c>stream:a0</c> (sound alone). OB2 downloads it itself. yt-dlp's
/// format ids never contain a colon (D-74), so the prefix cannot clash.
/// </summary>
public static partial class StreamSpec
{
    public const string Prefix = "stream:";

    public static bool IsSpec(string? spec) => spec is not null && Pattern().IsMatch(spec);

    public static string For(string ids) => Prefix + ids;

    public static bool TryParse(string? spec, out string? video, out string? audio)
    {
        video = audio = null;
        var match = spec is null ? Match.Empty : Pattern().Match(spec);
        if (!match.Success)
        {
            return false;
        }

        video = match.Groups["v"].Success ? match.Groups["v"].Value : null;
        audio = match.Groups["a"].Success ? match.Groups["a"].Value : match.Groups["solo"].Success ? match.Groups["solo"].Value : null;
        return true;
    }

    /// <summary>Mirrors <c>ProtocolValidator.StreamSpec</c> in OB2.Shared.</summary>
    [GeneratedRegex(@"^stream:(?:(?<v>v\d{1,3})(?:\+(?<a>a\d{1,3}))?|(?<solo>a\d{1,3}))\z")]
    private static partial Regex Pattern();
}

/// <summary>
/// D-80: a stream's manifest as the page's player requested it. The browser's headers go with
/// every request for it and its pieces, because players' servers check them: movie-box.co's
/// answered 403 or 429 to anything without its <c>X-MB-Token</c>, <c>Origin</c> and <c>Referer</c>.
/// </summary>
public sealed record StreamRequest(Uri Url, string? Referrer, string? UserAgent, string? Cookies, IReadOnlyDictionary<string, string> Headers)
{
    public static StreamRequest Plain(Uri url, string? referrer = null) => new(url, referrer, null, null, new Dictionary<string, string>());

    public RequestContext Context(string defaultUserAgent) => new(UserAgent ?? defaultUserAgent, Referrer, Cookies, Headers);

    /// <summary>An address that names a manifest; anything else is known to be a stream only from the browser.</summary>
    public static bool LooksLikeManifest(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var ext = Path.GetExtension(url.AbsolutePath);
        return ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mpd", StringComparison.OrdinalIgnoreCase);
    }
}
