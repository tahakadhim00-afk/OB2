using System.Globalization;
using System.Text.Json;

namespace OB2.Core.Media;

/// <summary>What yt-dlp reports about a video page (<c>yt-dlp -J</c>), reduced to what OB2 uses.</summary>
public sealed record MediaInfo(string Title, string? Site, TimeSpan? Duration, IReadOnlyList<MediaFormat> Formats)
{
    /// <summary>D-74: the subtitle tracks OB2 can save, the site's own first; empty when there are none.</summary>
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = [];

    /// <summary>
    /// D-79: yt-dlp has no extractor for the site and guessed from the page's HTML, or read a
    /// file or stream address directly.
    /// </summary>
    public bool IsGeneric => string.Equals(Site, "Generic", StringComparison.Ordinal);

    /// <summary>D-80: OB2 read a stream's manifest itself; its formats download without yt-dlp.</summary>
    public bool FromStream { get; init; }

    /// <exception cref="MediaException">Not a single video, or not yt-dlp's JSON.</exception>
    public static MediaInfo Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new MediaException("yt-dlp gave an answer OB2 could not read.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new MediaException("yt-dlp gave an answer OB2 could not read.");
            }

            if (Str(root, "_type") is "playlist" or "multi_video")
            {
                throw new MediaException("This address is a playlist. Open a single video and try again.");
            }

            var formats = new List<MediaFormat>();
            if (root.TryGetProperty("formats", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                formats.AddRange(list.EnumerateArray().Select(ReadFormat).OfType<MediaFormat>());
            }
            else if (ReadFormat(root) is { } single)
            {
                formats.Add(single); // sites with one file report it on the root
            }

            var title = Str(root, "title") is { Length: > 0 } t ? t : "video";
            var duration = Num(root, "duration") is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
            return new MediaInfo(title, Str(root, "extractor_key"), duration, formats) { Subtitles = SubtitleTrack.Read(root) };
        }
    }

    private static MediaFormat? ReadFormat(JsonElement e)
    {
        var id = Str(e, "format_id");
        var ext = Str(e, "ext");
        if (id is null || ext is null || ext == "mhtml")
        {
            return null; // storyboards (thumbnail sheets) are not media
        }

        var format = new MediaFormat
        {
            Id = id,
            Ext = ext,
            VideoCodec = Str(e, "vcodec"),
            AudioCodec = Str(e, "acodec"),
            Height = (int?)Num(e, "height"),
            Width = (int?)Num(e, "width"),
            Fps = Num(e, "fps"),
            Bitrate = Num(e, "tbr"),
            AudioBitrate = Num(e, "abr"),
            Size = (long?)(Num(e, "filesize") ?? Num(e, "filesize_approx")),
            Note = Str(e, "format_note"),
            Language = Str(e, "language"),
            DynamicRange = Str(e, "dynamic_range"),
        };
        return format.HasVideo || format.HasAudio ? format : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>One format of a video page: a video track, an audio track, or both (yt-dlp's names).</summary>
public sealed record MediaFormat
{
    public required string Id { get; init; }

    public required string Ext { get; init; }

    /// <summary><c>none</c> for an audio-only format; null when the site doesn't say.</summary>
    public string? VideoCodec { get; init; }

    /// <summary><c>none</c> for a video-only format; null when the site doesn't say.</summary>
    public string? AudioCodec { get; init; }

    public int? Height { get; init; }

    public int? Width { get; init; }

    public double? Fps { get; init; }

    /// <summary>Total bitrate in kbit/s.</summary>
    public double? Bitrate { get; init; }

    public double? AudioBitrate { get; init; }

    /// <summary>Exact or approximate size in bytes.</summary>
    public long? Size { get; init; }

    public string? Note { get; init; }

    public string? Language { get; init; }

    public string? DynamicRange { get; init; }

    /// <summary>D-80: a track of a stream OB2 reads itself (<see cref="StreamManifest"/>), not a yt-dlp format.</summary>
    public bool FromStream { get; init; }

    public bool HasVideo => VideoCodec != "none";

    public bool HasAudio => AudioCodec != "none";

    /// <summary>"1080p60", "720p", or the site's own note when there is no height.</summary>
    public string Quality =>
        Height is { } h
            ? string.Create(CultureInfo.InvariantCulture, $"{h}p{(Fps is >= 48 ? Math.Round(Fps.Value).ToString(CultureInfo.InvariantCulture) : string.Empty)}{(IsHdr ? " HDR" : string.Empty)}")
            : Note ?? Id;

    public bool IsHdr => DynamicRange is { } d && !string.Equals(d, "SDR", StringComparison.OrdinalIgnoreCase);

    public string? VideoCodecName => CodecName(VideoCodec, VideoNames);

    public string? AudioCodecName => CodecName(AudioCodec, AudioNames);

    private static readonly (string Prefix, string Name)[] VideoNames =
        [("avc", "H.264"), ("hev", "H.265"), ("hvc", "H.265"), ("vp09", "VP9"), ("vp9", "VP9"), ("vp8", "VP8"), ("av01", "AV1")];

    private static readonly (string Prefix, string Name)[] AudioNames =
        [("mp4a", "AAC"), ("aac", "AAC"), ("opus", "Opus"), ("vorbis", "Vorbis"), ("mp3", "MP3"), ("flac", "FLAC"), ("ac-3", "AC-3"), ("ec-3", "E-AC-3")];

    private static string? CodecName(string? codec, (string Prefix, string Name)[] names) =>
        codec is null or "none" ? null : names.FirstOrDefault(n => codec.StartsWith(n.Prefix, StringComparison.OrdinalIgnoreCase)).Name ?? codec;
}

/// <summary>yt-dlp could not list or download a video; the message is meant for the user.</summary>
public sealed class MediaException : Exception
{
    public MediaException()
    {
    }

    public MediaException(string message)
        : base(message)
    {
    }

    public MediaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>D-79: yt-dlp has no extractor for the site, and the page's HTML named no video it could use.</summary>
    public bool Unsupported { get; init; }

    /// <summary>D-80: the stream itself rules the download out (copy-protected, or live); asking yt-dlp as well would only take longer.</summary>
    public bool Conclusive { get; init; }
}
