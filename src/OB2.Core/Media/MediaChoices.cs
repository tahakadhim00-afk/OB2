using System.Globalization;

namespace OB2.Core.Media;

/// <summary>What the user picked, as yt-dlp needs it.</summary>
/// <param name="FormatSpec">yt-dlp's <c>-f</c> value: one format id, or <c>video+audio</c>; or a subtitle track's <see cref="SubtitleTrack.Spec"/> (D-74).</param>
/// <param name="Container">The finished file's extension.</param>
/// <param name="EstimatedSize">Sum of the known part sizes; null when none is known.</param>
public sealed record MediaSelection(string FormatSpec, string Container, long? EstimatedSize)
{
    /// <summary>D-74: one subtitle track, not picture or sound.</summary>
    public bool IsSubtitles => SubtitleTrack.IsSpec(FormatSpec);

    /// <summary>
    /// "Clip.mp4". Subtitles carry their language, "Clip.en.srt", so a player that opens
    /// Clip.mp4 picks them up from the same folder.
    /// </summary>
    public string FileName(string stem) =>
        SubtitleTrack.TryParseSpec(FormatSpec, out var language, out _) ? $"{stem}.{language}.{Container}" : $"{stem}.{Container}";

    /// <summary>The title part of a name <see cref="FileName"/> made; any other name without its extension.</summary>
    public string Stem(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var suffix = FileName(string.Empty);
        return fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }
}

/// <summary>
/// One entry of the quality menu on the browser's video button (D-72): a picture quality with the
/// sound that suits it, or a sound track alone.
/// </summary>
/// <param name="Quality">"1080p60" for a picture, "129 kbit/s" for sound alone; null when unknown.</param>
/// <param name="Codec">The picture's codec ("H.264"), or the sound's when there is no picture.</param>
/// <param name="Track">The sound track's name, only when the video has tracks in several languages.</param>
public sealed record MediaOption(MediaSelection Selection, bool AudioOnly, string? Quality, string? Codec, string? Track);

/// <summary>
/// Turns yt-dlp's format list into the choices of the Download Video window (D-71): one entry
/// per picture quality, one per audio track, and what a pair of them means as a download.
/// </summary>
public static class MediaChoices
{
    /// <summary>
    /// The best format for each height and frame rate (HDR listed apart), best first. At equal
    /// quality MP4/H.264 wins, because it merges into an MP4 every player opens.
    /// </summary>
    public static IReadOnlyList<MediaFormat> Videos(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return [.. info.Formats
            .Where(f => f.HasVideo)
            .GroupBy(f => (f.Height ?? 0, FpsClass(f), f.IsHdr))
            .Select(g => g
                .OrderByDescending(f => f.Ext == "mp4" && f.VideoCodecName == "H.264")
                .ThenByDescending(f => f.Bitrate ?? 0)
                .First())
            .OrderByDescending(f => f.Height ?? 0)
            .ThenByDescending(f => FpsClass(f))
            .ThenBy(f => f.IsHdr)];
    }

    /// <summary>
    /// Audio-only formats: the best one per language and file type, the site's default track
    /// first, then by bitrate.
    /// </summary>
    public static IReadOnlyList<MediaFormat> Audios(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return [.. info.Formats
            .Where(f => f.HasAudio && !f.HasVideo)
            .GroupBy(f => (f.Language ?? string.Empty, f.Ext))
            .Select(g => g.OrderByDescending(Bitrate).First())
            .OrderByDescending(IsDefaultTrack)
            .ThenByDescending(Bitrate)];
    }

    /// <summary>The audio track that suits <paramref name="video"/>: same file type when possible, so it merges into MP4/WebM.</summary>
    public static MediaFormat? AudioFor(MediaFormat? video, IReadOnlyList<MediaFormat> audios)
    {
        ArgumentNullException.ThrowIfNull(audios);
        if (audios.Count == 0)
        {
            return null;
        }

        var defaults = audios.Where(IsDefaultTrack).ToList();
        var pool = defaults.Count > 0 ? defaults : [.. audios];
        return pool.FirstOrDefault(a => MergesInto(video, a) is "mp4" or "webm") ?? pool[0];
    }

    /// <summary>
    /// <paramref name="video"/> null means audio only. A video format that carries its own sound
    /// is downloaded alone; a picture-only one is merged with <paramref name="audio"/>.
    /// </summary>
    public static MediaSelection Select(MediaFormat? video, MediaFormat? audio)
    {
        if (video is null)
        {
            ArgumentNullException.ThrowIfNull(audio);
            return new MediaSelection(Spec(audio, audio.Id), audio.Ext, audio.Size);
        }

        if (video.HasAudio || audio is null)
        {
            return new MediaSelection(Spec(video, video.Id), video.Ext, video.Size);
        }

        long? size = video.Size is null && audio.Size is null ? null : (video.Size ?? 0) + (audio.Size ?? 0);
        return new MediaSelection(Spec(video, $"{video.Id}+{audio.Id}"), MergesInto(video, audio), size);
    }

    /// <summary>D-80: tracks OB2 read from a stream itself are downloaded by OB2, not yt-dlp.</summary>
    private static string Spec(MediaFormat format, string ids) => format.FromStream ? StreamSpec.For(ids) : ids;

    /// <summary>D-74: a subtitle track saved as <paramref name="format"/> (one of <see cref="SubtitleTrack.Formats"/>).</summary>
    public static MediaSelection Select(SubtitleTrack track, string format)
    {
        ArgumentNullException.ThrowIfNull(track);
        return SubtitleTrack.Formats.Contains(format)
            ? new MediaSelection(track.Spec, format, null)
            : throw new ArgumentException($"Subtitles are saved as {string.Join(", ", SubtitleTrack.Formats)}.", nameof(format));
    }

    /// <summary>
    /// The file a picture and a sound track merge into. YouTube's HLS sound is "mp4" (raw AAC)
    /// and its HLS picture an MPEG-TS stream named "mp4": ffmpeg merges those into MP4 but fails
    /// on MKV ("Can't write packet with unknown timestamp"), so MP4 wins whenever both fit (D-73).
    /// </summary>
    private static string MergesInto(MediaFormat? video, MediaFormat audio) => (video?.Ext, audio.Ext) switch
    {
        ("mp4", "m4a" or "mp4") => "mp4",
        ("webm", "webm") => "webm",
        _ => "mkv", // holds any pair of codecs
    };

    /// <summary>
    /// The button's menu (D-72): every picture quality with the site's default sound, best first,
    /// then sound alone, one entry per language, as M4A when there is one (it plays everywhere).
    /// Other pairs stay in the Download Video window.
    /// </summary>
    public static IReadOnlyList<MediaOption> Options(MediaInfo info)
    {
        var audios = Audios(info);
        var options = Videos(info)
            .Select(video => new MediaOption(Select(video, AudioFor(video, audios)), false, video.Quality, video.VideoCodecName, null))
            .ToList();

        // Audios lists the default track first, and grouping keeps that order.
        var languages = audios.GroupBy(a => a.Language ?? string.Empty).ToList();
        foreach (var language in languages)
        {
            var audio = language.FirstOrDefault(a => a.Ext == "m4a") ?? language.First();
            var kbps = Bitrate(audio) is > 0 and var rate ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(rate)} kbit/s") : null;
            options.Add(new MediaOption(Select(null, audio), true, kbps, audio.AudioCodecName, languages.Count > 1 ? TrackName(audio) : null));
        }

        return options;
    }

    private static int FpsClass(MediaFormat f) => f.Fps is >= 48 ? (int)Math.Round(f.Fps.Value) : 0;

    /// <summary>"English original (default), medium" without the ", medium" quality word.</summary>
    private static string? TrackName(MediaFormat f)
    {
        var note = f.Note;
        var comma = note?.LastIndexOf(", ", StringComparison.Ordinal) ?? -1;
        if (note is not null && comma > 0 && note[(comma + 2)..] is "ultralow" or "low" or "medium" or "high")
        {
            note = note[..comma];
        }

        return note is { Length: > 0 } and not ("ultralow" or "low" or "medium" or "high") ? note : f.Language;
    }

    private static double Bitrate(MediaFormat f) => f.AudioBitrate ?? f.Bitrate ?? 0;

    /// <summary>YouTube marks the original or default track in the note: "English (original), default".</summary>
    private static bool IsDefaultTrack(MediaFormat f) =>
        f.Note is { } note && (note.Contains("default", StringComparison.OrdinalIgnoreCase) || note.Contains("original", StringComparison.OrdinalIgnoreCase));
}
