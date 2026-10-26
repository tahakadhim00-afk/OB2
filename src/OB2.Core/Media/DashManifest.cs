using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace OB2.Core.Media;

/// <summary>
/// D-80: DASH manifests (ISO/IEC 23009-1, the <c>.mpd</c> a player loads). Each picture quality
/// and sound track is a Representation whose pieces come from a SegmentTemplate (numbered or
/// timed, as ffmpeg and most packagers write them), a SegmentList, or a single file. A track
/// under ContentProtection is DRM and left out; a dynamic manifest is a live broadcast.
/// </summary>
public static partial class DashManifest
{
    public static bool IsDash(string text) => text.Contains("<MPD", StringComparison.Ordinal);

    /// <exception cref="MediaException">Not DASH, copy-protected, live, or nothing OB2 can read.</exception>
    public static StreamManifest Parse(string xml, Uri url)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(url);
        XElement mpd;
        try
        {
            // A manifest is data from the web: no DTD, no external entities.
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            mpd = XDocument.Load(reader).Root ?? throw new MediaException("The DASH manifest is empty.");
        }
        catch (XmlException ex)
        {
            throw new MediaException("The DASH manifest is not valid XML.", ex);
        }

        if (mpd.Name.LocalName != "MPD")
        {
            throw new MediaException("The address is not a DASH manifest.");
        }

        if (Attr(mpd, "type") == "dynamic")
        {
            throw new MediaException(HlsPlaylist.Live) { Conclusive = true };
        }

        var total = Duration(Attr(mpd, "mediaPresentationDuration"));
        var mpdBase = Resolve(url, mpd);
        var period = LongestPeriod(mpd, total) ?? throw new MediaException("The DASH manifest has no period.");
        var length = Duration(Attr(period.Element, "duration")) ?? period.Length;
        var periodBase = Resolve(mpdBase, period.Element);

        var tracks = new List<StreamTrack>();
        var protectedTracks = 0;
        int videos = 0, audios = 0;
        foreach (var set in Children(period.Element, "AdaptationSet"))
        {
            var setBase = Resolve(periodBase, set);
            foreach (var rep in Children(set, "Representation"))
            {
                var mime = Attr(rep, "mimeType") ?? Attr(set, "mimeType") ?? string.Empty;
                var codecs = Attr(rep, "codecs") ?? Attr(set, "codecs");
                var kind = Attr(set, "contentType") ?? Attr(rep, "contentType") ?? mime.Split('/')[0];
                if (kind is not ("video" or "audio"))
                {
                    continue; // subtitles, thumbnails
                }

                if (Children(set, "ContentProtection").Any() || Children(rep, "ContentProtection").Any())
                {
                    protectedTracks++;
                    continue;
                }

                var video = kind == "video";
                var webm = mime.EndsWith("/webm", StringComparison.OrdinalIgnoreCase);
                var parts = codecs?.Split(',', StringSplitOptions.TrimEntries) ?? [];
                tracks.Add(new StreamTrack
                {
                    Id = video ? $"v{videos++}" : $"a{audios++}",
                    Ext = webm ? "webm" : video ? "mp4" : "m4a",
                    HasVideo = video,
                    // A picture track with sound muxed in names both codecs.
                    HasAudio = !video || parts.Length > 1,
                    VideoCodec = video ? parts.FirstOrDefault() : null,
                    AudioCodec = video ? parts.Skip(1).FirstOrDefault() : parts.FirstOrDefault(),
                    Width = Int(Attr(rep, "width") ?? Attr(set, "width")),
                    Height = Int(Attr(rep, "height") ?? Attr(set, "height")),
                    Fps = FrameRate(Attr(rep, "frameRate") ?? Attr(set, "frameRate")),
                    Bandwidth = long.TryParse(Attr(rep, "bandwidth"), NumberStyles.None, CultureInfo.InvariantCulture, out var bw) ? bw : null,
                    Language = Attr(set, "lang") is { } lang && lang != "und" ? lang : null,
                    Name = video ? null : Label(set),
                    Pieces = Pieces(rep, set, period.Element, Resolve(setBase, rep), length),
                });
            }
        }

        if (tracks.Count == 0)
        {
            throw protectedTracks > 0
                ? new MediaException(HlsPlaylist.Protected) { Conclusive = true }
                : new MediaException("The DASH manifest has no picture or sound OB2 can read.");
        }

        return new StreamManifest(StreamKind.Dash, url, length ?? total, tracks);
    }

    private static List<StreamPiece> Pieces(XElement rep, XElement set, XElement period, Uri repBase, TimeSpan? length)
    {
        XElement?[] templates = [Child(rep, "SegmentTemplate"), Child(set, "SegmentTemplate"), Child(period, "SegmentTemplate")];
        if (templates.Any(t => t is not null))
        {
            return FromTemplate(templates, rep, repBase, length);
        }

        if ((Child(rep, "SegmentList") ?? Child(set, "SegmentList") ?? Child(period, "SegmentList")) is { } list)
        {
            var pieces = new List<StreamPiece>();
            if (Child(list, "Initialization") is { } init)
            {
                pieces.Add(Piece(repBase, Attr(init, "sourceURL"), Attr(init, "range"), isInit: true));
            }

            pieces.AddRange(Children(list, "SegmentURL").Select(s => Piece(repBase, Attr(s, "media"), Attr(s, "mediaRange"), isInit: false)));
            return pieces;
        }

        // SegmentBase, or nothing: the track is one file, its BaseURL.
        return Child(rep, "BaseURL") is not null
            ? [new StreamPiece(repBase, null, null, null)]
            : throw new MediaException("The DASH manifest does not say where a track's pieces are.");
    }

    private static List<StreamPiece> FromTemplate(XElement?[] templates, XElement rep, Uri repBase, TimeSpan? length)
    {
        string? T(string name) => templates.Select(t => t is null ? null : Attr(t, name)).FirstOrDefault(v => v is not null);
        var media = T("media") ?? throw new MediaException("The DASH manifest's template names no pieces.");
        var timescale = long.TryParse(T("timescale"), NumberStyles.None, CultureInfo.InvariantCulture, out var ts) && ts > 0 ? ts : 1;
        var number = long.TryParse(T("startNumber"), NumberStyles.None, CultureInfo.InvariantCulture, out var sn) ? sn : 1;
        var id = Attr(rep, "id") ?? string.Empty;
        var bandwidth = Attr(rep, "bandwidth") ?? string.Empty;
        var pieces = new List<StreamPiece>();
        if (T("initialization") is { } init)
        {
            pieces.Add(new StreamPiece(new Uri(repBase, Fill(init, id, bandwidth, 0, 0)), null, null, null, IsInit: true));
        }

        var timeline = templates.Select(t => t is null ? null : Child(t, "SegmentTimeline")).FirstOrDefault(t => t is not null);
        if (timeline is not null)
        {
            var end = length is { } l ? (long)(l.TotalSeconds * timescale) + (long.TryParse(T("presentationTimeOffset"), CultureInfo.InvariantCulture, out var pto) ? pto : 0) : long.MaxValue;
            long time = 0;
            foreach (var s in Children(timeline, "S"))
            {
                time = long.TryParse(Attr(s, "t"), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : time;
                var d = long.TryParse(Attr(s, "d"), NumberStyles.None, CultureInfo.InvariantCulture, out var dv) && dv > 0 ? dv : throw new MediaException("The DASH manifest's timeline has a piece without a length.");
                var repeat = long.TryParse(Attr(s, "r"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r) ? r : 0;
                // r = -1: repeat until the period ends (or the next S starts).
                for (long i = 0; repeat < 0 ? time < end : i <= repeat; i++)
                {
                    pieces.Add(new StreamPiece(new Uri(repBase, Fill(media, id, bandwidth, number++, time)), null, null, null));
                    time += d;
                    if (repeat < 0 && end == long.MaxValue)
                    {
                        break; // no end to repeat to
                    }
                }
            }

            return pieces;
        }

        var duration = long.TryParse(T("duration"), NumberStyles.None, CultureInfo.InvariantCulture, out var dur) && dur > 0 ? dur : 0;
        if (duration == 0 || length is null)
        {
            throw new MediaException("The DASH manifest does not say how many pieces a track has.");
        }

        var count = (long)Math.Ceiling(Math.Round(length.Value.TotalSeconds * timescale / duration, 6));
        for (long i = 0; i < count; i++)
        {
            pieces.Add(new StreamPiece(new Uri(repBase, Fill(media, id, bandwidth, number + i, i * duration)), null, null, null));
        }

        return pieces;
    }

    /// <summary><c>$RepresentationID$</c>, <c>$Number$</c>, <c>$Number%05d$</c>, <c>$Bandwidth$</c>, <c>$Time$</c>, and <c>$$</c> for a dollar.</summary>
    private static string Fill(string template, string id, string bandwidth, long number, long time) =>
        TemplatePattern().Replace(template, m =>
        {
            var width = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            string Pad(long v) => v.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
            return m.Groups[1].Value switch
            {
                "RepresentationID" => id,
                "Bandwidth" => bandwidth,
                "Number" => Pad(number),
                "Time" => Pad(time),
                _ => "$",
            };
        });

    private static StreamPiece Piece(Uri repBase, string? media, string? range, bool isInit)
    {
        var url = media is null ? repBase : new Uri(repBase, media);
        if (range is null)
        {
            return new StreamPiece(url, null, null, null, isInit);
        }

        var dash = range.IndexOf('-', StringComparison.Ordinal);
        var from = long.Parse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture);
        var to = long.Parse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture);
        return new StreamPiece(url, from, to - from + 1, null, isInit);
    }

    /// <summary>Ads are periods of their own; the film is the longest one.</summary>
    private static (XElement Element, TimeSpan? Length)? LongestPeriod(XElement mpd, TimeSpan? total)
    {
        var periods = Children(mpd, "Period").ToList();
        var starts = periods.Select(p => Duration(Attr(p, "start"))).ToList();
        (XElement, TimeSpan?)? best = null;
        for (var i = 0; i < periods.Count; i++)
        {
            var length = Duration(Attr(periods[i], "duration"))
                ?? (i + 1 < periods.Count && starts[i + 1] is { } next ? next - (starts[i] ?? TimeSpan.Zero) : total - (starts[i] ?? TimeSpan.Zero));
            if (best is null || (length ?? TimeSpan.Zero) > (best.Value.Item2 ?? TimeSpan.Zero))
            {
                best = (periods[i], length);
            }
        }

        return best;
    }

    private static string? Label(XElement set)
    {
        var label = Child(set, "Label")?.Value.Trim() ?? Attr(set, "label") ?? Attr(set, "lang");
        var main = Children(set, "Role").Any(r => Attr(r, "value") == "main");
        return label is { Length: > 0 } && label != "und" ? (main ? $"{label}, default" : label) : main ? "default" : null;
    }

    private static Uri Resolve(Uri parent, XElement element) =>
        Child(element, "BaseURL")?.Value.Trim() is { Length: > 0 } baseUrl ? new Uri(parent, baseUrl) : parent;

    private static TimeSpan? Duration(string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static double? FrameRate(string? value)
    {
        var slash = value?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        if (slash > 0 && double.TryParse(value![..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(value[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
        {
            return n / d;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ? fps : null;
    }

    private static int? Int(string? value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement e, string name) => e.Elements().Where(c => c.Name.LocalName == name);

    [GeneratedRegex(@"\$(RepresentationID|Number|Bandwidth|Time|)(?:%0(\d{1,2})d)?\$")]
    private static partial Regex TemplatePattern();
}
