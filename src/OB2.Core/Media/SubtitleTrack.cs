using System.Text.Json;
using System.Text.RegularExpressions;

namespace OB2.Core.Media;

/// <summary>
/// D-74: a subtitle track of a video page that OB2 can save as its own download.
/// <paramref name="Automatic"/>: made by the site's speech recognition (YouTube's auto-generated
/// captions) rather than uploaded with the video.
/// </summary>
/// <param name="Language">yt-dlp's key for the track: <c>en</c>, <c>pt-BR</c>, <c>en-orig</c>.</param>
/// <param name="Name">The site's name for it ("English", "German (Germany)").</param>
public sealed partial record SubtitleTrack(string Language, string Name, bool Automatic)
{
    public const string SitePrefix = "subs:";
    public const string AutoPrefix = "auto-subs:";

    /// <summary>What OB2 saves subtitles as. yt-dlp converts with ffmpeg when the site has another format.</summary>
    public static IReadOnlyList<string> Formats { get; } = ["srt", "ass", "vtt"];

    /// <summary>Formats ffmpeg can convert from. YouTube's json3/srv/ttml can't, and live chat is not subtitles.</summary>
    private static readonly HashSet<string> Readable = new(StringComparer.OrdinalIgnoreCase) { "srt", "vtt", "ass", "ssa" };

    /// <summary>
    /// Stored and sent in place of a format selector: <c>subs:en</c>, <c>auto-subs:en-orig</c>,
    /// after yt-dlp's <c>--write-subs</c> and <c>--write-auto-subs</c>. Format ids never contain a colon.
    /// </summary>
    public string Spec => (Automatic ? AutoPrefix : SitePrefix) + Language;

    public static bool TryParseSpec(string? spec, out string language, out bool automatic)
    {
        automatic = spec?.StartsWith(AutoPrefix, StringComparison.Ordinal) == true;
        language = spec is null ? string.Empty
            : automatic ? spec[AutoPrefix.Length..]
            : spec.StartsWith(SitePrefix, StringComparison.Ordinal) ? spec[SitePrefix.Length..]
            : string.Empty;
        return IsLanguage(language);
    }

    public static bool IsSpec(string? spec) => TryParseSpec(spec, out _, out _);

    /// <summary>
    /// A key yt-dlp takes literally: <c>--sub-langs</c> reads regular expressions, a leading
    /// <c>-</c> as "not" and commas as lists, so only letters, digits, <c>_</c> and <c>-</c> pass.
    /// </summary>
    public static bool IsLanguage(string language) => LanguageKey().IsMatch(language);

    /// <summary>
    /// The tracks in <c>yt-dlp -J</c> output: the site's own (<c>subtitles</c>), then the
    /// auto-generated ones (<c>automatic_captions</c>) in languages the site has none in. Left out:
    /// YouTube's machine translations (their addresses carry <c>tlang=</c>; YouTube answers them
    /// with HTTP 429), the <c>en-orig</c> twin of an <c>en</c> track, and tracks with no format
    /// ffmpeg reads.
    /// </summary>
    internal static IReadOnlyList<SubtitleTrack> Read(JsonElement root)
    {
        var site = Tracks(root, "subtitles").ToList();
        var taken = site.Select(t => t.Language).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var auto = Tracks(root, "automatic_captions")
            .Where(t => !t.Translated && !taken.Contains(t.Language))
            .ToList();
        var autoKeys = auto.Select(t => t.Language).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. site.Select(t => new SubtitleTrack(t.Language, t.Name, false)),
            .. auto
                .Where(t => !(t.Language.EndsWith("-orig", StringComparison.OrdinalIgnoreCase) && autoKeys.Contains(t.Language[..^"-orig".Length])))
                .Select(t => new SubtitleTrack(t.Language, t.Name, true)),
        ];
    }

    private static IEnumerable<(string Language, string Name, bool Translated)> Tracks(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var tracks) || tracks.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var track in tracks.EnumerateObject())
        {
            if (!IsLanguage(track.Name) || track.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var entries = track.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            if (!entries.Any(e => Str(e, "ext") is { } ext && Readable.Contains(ext)))
            {
                continue;
            }

            var name = entries.Select(e => Str(e, "name")).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? track.Name;
            yield return (track.Name, name.Trim(), entries.Any(e => HasQueryParameter(Str(e, "url"), "tlang")));
        }
    }

    private static bool HasQueryParameter(string? url, string name)
    {
        var query = url is null ? -1 : url.IndexOf('?', StringComparison.Ordinal);
        return query >= 0 && url![(query + 1)..].Split('&').Any(p => p.StartsWith(name + "=", StringComparison.Ordinal));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,34}\z")]
    private static partial Regex LanguageKey();
}
