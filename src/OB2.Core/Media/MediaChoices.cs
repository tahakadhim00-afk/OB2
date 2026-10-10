namespace OB2.Core.Media;

/// <summary>What the user picked, as yt-dlp needs it.</summary>
/// <param name="FormatSpec">yt-dlp's <c>-f</c> value: one format id, or <c>video+audio</c>.</param>
/// <param name="Container">The finished file's extension.</param>
/// <param name="EstimatedSize">Sum of the known part sizes; null when none is known.</param>
public sealed record MediaSelection(string FormatSpec, string Container, long? EstimatedSize);

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

        var wanted = video?.Ext switch
        {
            "mp4" => "m4a",
            "webm" => "webm",
            _ => null,
        };
        var defaults = audios.Where(IsDefaultTrack).ToList();
        var pool = defaults.Count > 0 ? defaults : [.. audios];
        return pool.FirstOrDefault(a => a.Ext == wanted) ?? pool[0];
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
            return new MediaSelection(audio.Id, audio.Ext, audio.Size);
        }

        if (video.HasAudio || audio is null)
        {
            return new MediaSelection(video.Id, video.Ext, video.Size);
        }

        var container = (video.Ext, audio.Ext) switch
        {
            ("mp4", "m4a") => "mp4",
            ("webm", "webm") => "webm",
            _ => "mkv", // holds any pair of codecs
        };
        long? size = video.Size is null && audio.Size is null ? null : (video.Size ?? 0) + (audio.Size ?? 0);
        return new MediaSelection($"{video.Id}+{audio.Id}", container, size);
    }

    private static int FpsClass(MediaFormat f) => f.Fps is >= 48 ? (int)Math.Round(f.Fps.Value) : 0;

    private static double Bitrate(MediaFormat f) => f.AudioBitrate ?? f.Bitrate ?? 0;

    /// <summary>YouTube marks the original or default track in the note: "English (original), default".</summary>
    private static bool IsDefaultTrack(MediaFormat f) =>
        f.Note is { } note && (note.Contains("default", StringComparison.OrdinalIgnoreCase) || note.Contains("original", StringComparison.OrdinalIgnoreCase));
}
