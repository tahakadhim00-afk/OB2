using System.Globalization;
using System.Text.RegularExpressions;

namespace OB2.Core.Media;

/// <summary>
/// D-80: HLS playlists (RFC 8216). A master playlist names the picture qualities and the sound
/// tracks; a media playlist lists one track's pieces. Encrypted pieces are AES-128 with a key the
/// playlist hands out, as to any player; SAMPLE-AES and key systems (FairPlay, Widevine) are DRM.
/// </summary>
public static partial class HlsPlaylist
{
    internal const string Protected = "This video is copy-protected (DRM), so OB2 cannot download it.";
    internal const string Live = "This is a live broadcast. OB2 downloads recorded videos, not live ones.";

    public static bool IsHls(string text) => text.AsSpan().TrimStart("﻿ \t\r\n").StartsWith("#EXTM3U", StringComparison.Ordinal);

    /// <summary>
    /// The tracks of a master playlist, whose pieces are read later from each one's playlist; or
    /// the single track of a media playlist, pieces included.
    /// </summary>
    /// <exception cref="MediaException">Not HLS, copy-protected, live, or nothing to download.</exception>
    public static StreamManifest Parse(string text, Uri url)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(url);
        if (!IsHls(text))
        {
            throw new MediaException("The address is not an HLS playlist.");
        }

        var lines = Lines(text).ToList();
        if (!lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)))
        {
            var media = ParseMedia(text, url);
            // Nothing says what it holds: one quality, picture and sound together.
            var track = new StreamTrack { Id = "v0", HasVideo = true, HasAudio = true, Ext = "mp4", Name = "Video", Pieces = media.Pieces };
            return new StreamManifest(StreamKind.Hls, url, media.Duration, [track]);
        }

        return ParseMaster(lines, url);
    }

    /// <summary>One track's pieces and the time they add up to.</summary>
    public sealed record MediaPlaylist(IReadOnlyList<StreamPiece> Pieces, TimeSpan Duration);

    /// <exception cref="MediaException">Copy-protected, live, changes its format midway, or lists no pieces.</exception>
    public static MediaPlaylist ParseMedia(string text, Uri url)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(url);
        var pieces = new List<StreamPiece>();
        long sequence = 0;
        double seconds = 0;
        var ended = false;
        var vod = false;
        StreamKey? key = null;
        var keyHasIv = false;
        Uri? initUrl = null;
        (long? Length, long? Offset) range = (null, null);
        var nextOffset = new Dictionary<Uri, long>();

        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                sequence = long.TryParse(line.AsSpan(22), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var a = Attributes(line);
                var method = a.GetValueOrDefault("METHOD", "NONE");
                if (method == "NONE")
                {
                    key = null;
                }
                else if (method == "AES-128" && a.GetValueOrDefault("KEYFORMAT", "identity") == "identity" && a.TryGetValue("URI", out var keyUri))
                {
                    keyHasIv = a.TryGetValue("IV", out var iv);
                    key = new StreamKey(new Uri(url, keyUri), keyHasIv ? Iv(iv!) : new byte[16]);
                }
                else
                {
                    throw new MediaException(Protected) { Conclusive = true };
                }
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var a = Attributes(line);
                var map = new Uri(url, a.GetValueOrDefault("URI") ?? throw new MediaException("The playlist names an initialization section without an address."));
                if (initUrl is null)
                {
                    initUrl = map;
                    var (length, offset) = a.TryGetValue("BYTERANGE", out var r) ? ByteRange(r) : (null, null);
                    pieces.Add(new StreamPiece(map, offset ?? (length is null ? null : 0), length, null, IsInit: true));
                }
                else if (map != initUrl)
                {
                    throw new MediaException("The stream changes its format midway, which OB2 cannot join.");
                }
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line.AsSpan(8);
                var comma = value.IndexOf(',');
                if (double.TryParse(comma >= 0 ? value[..comma] : value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                {
                    seconds += d;
                }
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                range = ByteRange(line[17..]);
            }
            else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal))
            {
                vod = line.EndsWith("VOD", StringComparison.Ordinal);
            }
            else if (line == "#EXT-X-ENDLIST")
            {
                ended = true;
            }
            else if (!line.StartsWith('#'))
            {
                var piece = new Uri(url, line);
                long? offset = null;
                if (range.Length is { } length)
                {
                    // Without an offset a range continues where the previous one in the same file ended.
                    offset = range.Offset ?? nextOffset.GetValueOrDefault(piece);
                    nextOffset[piece] = offset.Value + length;
                }

                // Without an IV the piece's sequence number is the IV (RFC 8216 §5.2).
                var pieceKey = key is null ? null : keyHasIv ? key : key with { Iv = SequenceIv(sequence) };
                pieces.Add(new StreamPiece(piece, offset, range.Length, pieceKey));
                range = (null, null);
                sequence++;
            }
        }

        if (!ended && !vod)
        {
            throw new MediaException(Live) { Conclusive = true };
        }

        return pieces.Any(p => !p.IsInit)
            ? new MediaPlaylist(pieces, TimeSpan.FromSeconds(seconds))
            : throw new MediaException("The stream's playlist lists no pieces.");
    }

    private static StreamManifest ParseMaster(List<string> lines, Uri url)
    {
        var audioGroups = new Dictionary<string, List<StreamTrack>>(StringComparer.Ordinal);
        var tracks = new List<StreamTrack>();
        var seen = new HashSet<Uri>();
        var audios = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
            {
                var a = Attributes(line);
                if (a.GetValueOrDefault("METHOD") is not ("NONE" or "AES-128") || a.GetValueOrDefault("KEYFORMAT", "identity") != "identity")
                {
                    throw new MediaException(Protected) { Conclusive = true };
                }
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var a = Attributes(line);
                // A rendition without an address plays inside the qualities themselves.
                if (a.GetValueOrDefault("TYPE") == "AUDIO" && a.TryGetValue("URI", out var uri) && seen.Add(new Uri(url, uri)))
                {
                    var name = a.GetValueOrDefault("NAME") ?? a.GetValueOrDefault("LANGUAGE");
                    var track = new StreamTrack
                    {
                        Id = $"a{audios++}",
                        Ext = "m4a",
                        HasAudio = true,
                        Language = a.GetValueOrDefault("LANGUAGE"),
                        Name = a.GetValueOrDefault("DEFAULT") == "YES" ? $"{name}, default" : name,
                        PlaylistUrl = new Uri(url, uri),
                    };
                    var group = a.GetValueOrDefault("GROUP-ID") ?? string.Empty;
                    (audioGroups.TryGetValue(group, out var list) ? list : audioGroups[group] = []).Add(track);
                }
            }
        }

        var videos = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                continue;
            }

            var a = Attributes(lines[i]);
            var uri = lines.Skip(i + 1).FirstOrDefault(l => !l.StartsWith('#'));
            if (uri is null || !seen.Add(new Uri(url, uri)))
            {
                continue; // the same quality listed again for another sound group
            }

            var codecs = (a.GetValueOrDefault("CODECS") ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var videoCodec = codecs.FirstOrDefault(c => !IsAudioCodec(c));
            var audioCodec = codecs.FirstOrDefault(IsAudioCodec);
            var separate = a.TryGetValue("AUDIO", out var group) && audioGroups.ContainsKey(group);
            if (codecs.Length > 0 && videoCodec is null)
            {
                // Sound alone, listed as a quality (radio and podcasts).
                tracks.Add(new StreamTrack { Id = $"a{audios++}", Ext = "m4a", HasAudio = true, AudioCodec = audioCodec, Bandwidth = Long(a, "BANDWIDTH"), PlaylistUrl = new Uri(url, uri) });
                continue;
            }

            var (width, height) = Resolution(a.GetValueOrDefault("RESOLUTION"));
            tracks.Add(new StreamTrack
            {
                Id = $"v{videos++}",
                Ext = "mp4",
                HasVideo = true,
                // Without CODECS nothing says the sound is missing, and usually it is inside.
                HasAudio = !separate && (codecs.Length == 0 || audioCodec is not null),
                VideoCodec = videoCodec,
                AudioCodec = separate ? null : audioCodec,
                Width = width,
                Height = height,
                Fps = double.TryParse(a.GetValueOrDefault("FRAME-RATE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ? fps : null,
                Bandwidth = Long(a, "AVERAGE-BANDWIDTH") ?? Long(a, "BANDWIDTH"),
                PlaylistUrl = new Uri(url, uri),
            });

            if (separate && audioCodec is not null)
            {
                for (var t = 0; t < audioGroups[group!].Count; t++)
                {
                    audioGroups[group!][t] = audioGroups[group!][t] with { AudioCodec = audioGroups[group!][t].AudioCodec ?? audioCodec };
                }
            }
        }

        tracks.AddRange(audioGroups.Values.SelectMany(g => g));
        return tracks.Count > 0
            ? new StreamManifest(StreamKind.Hls, url, null, [.. tracks.OrderBy(t => t.HasVideo ? 0 : 1)])
            : throw new MediaException("The stream's playlist names no qualities.");
    }

    private static bool IsAudioCodec(string codec) =>
        codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("ac-3", StringComparison.OrdinalIgnoreCase)
        || codec.StartsWith("ec-3", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("opus", StringComparison.OrdinalIgnoreCase)
        || codec.StartsWith("flac", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("mp3", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim().TrimStart('﻿')).Where(l => l.Length > 0);

    /// <summary><c>KEY=VALUE,KEY="quoted, with commas"</c>.</summary>
    private static Dictionary<string, string> Attributes(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in AttributePattern().Matches(colon < 0 ? string.Empty : line[(colon + 1)..]))
        {
            var value = m.Groups[2].Value;
            result[m.Groups[1].Value] = value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        }

        return result;
    }

    /// <summary><c>length[@offset]</c>.</summary>
    private static (long? Length, long? Offset) ByteRange(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        var length = long.Parse(at < 0 ? value : value[..at], NumberStyles.None, CultureInfo.InvariantCulture);
        return (length, at < 0 ? null : long.Parse(value[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture));
    }

    private static (int? Width, int? Height) Resolution(string? value)
    {
        var x = value?.IndexOf('x', StringComparison.OrdinalIgnoreCase) ?? -1;
        return x > 0 && int.TryParse(value![..x], CultureInfo.InvariantCulture, out var w) && int.TryParse(value[(x + 1)..], CultureInfo.InvariantCulture, out var h) ? (w, h) : (null, null);
    }

    private static long? Long(Dictionary<string, string> a, string name) =>
        a.TryGetValue(name, out var v) && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static byte[] Iv(string hex)
    {
        var digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        var bytes = Convert.FromHexString(digits.PadLeft(32, '0'));
        return bytes.Length == 16 ? bytes : throw new MediaException("The stream's encryption names an IV OB2 cannot read.");
    }

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequence);
        return iv;
    }

    [GeneratedRegex(@"([A-Z0-9-]+)=(""[^""]*""|[^,]*)")]
    private static partial Regex AttributePattern();
}
