using System.Net;
using System.Text;
using OB2.Core.Downloads;
using OB2.Core.Media;
using OB2.Tests.Common;

namespace OB2.Core.Tests.Media;

/// <summary>D-80: OB2 reads HLS and DASH streams itself. Nothing here needs yt-dlp or the network.</summary>
public sealed class HlsPlaylistTests
{
    private static readonly Uri Master = new("https://cdn.example/hls/master.m3u8?t=1");

    [Fact]
    public void A_master_playlist_lists_each_quality_once_and_its_separate_sound_tracks()
    {
        var manifest = HlsPlaylist.Parse("""
            #EXTM3U
            #EXT-X-VERSION:6
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",LANGUAGE="en",NAME="English",DEFAULT=YES,URI="audio/en/index.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",LANGUAGE="fr",NAME="Français, VF",URI="audio/fr/index.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=6000000,AVERAGE-BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",FRAME-RATE=25.000,AUDIO="aud"
            1080/index.m3u8?t=1
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720,CODECS="avc1.64001f,mp4a.40.2",AUDIO="aud"
            720/index.m3u8?t=1
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720,CODECS="avc1.64001f,mp4a.40.2",AUDIO="aud-hd"
            720/index.m3u8?t=1
            #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=100000,URI="iframe.m3u8"
            """, Master);

        manifest.Duration.Should().BeNull("a master playlist does not list pieces");
        manifest.Tracks.Select(t => t.Id).Should().Equal("v0", "v1", "a0", "a1");
        var hd = manifest.Tracks[0];
        (hd.Width, hd.Height, hd.Fps, hd.Bandwidth, hd.VideoCodec, hd.HasAudio).Should().Be((1920, 1080, 25.0, 5_000_000L, "avc1.640028", false));
        hd.PlaylistUrl.Should().Be(new Uri("https://cdn.example/hls/1080/index.m3u8?t=1"));
        var english = manifest.Tracks[2];
        (english.Language, english.Name, english.AudioCodec, english.HasVideo).Should().Be(("en", "English, default", "mp4a.40.2", false));
        manifest.Tracks[3].Name.Should().Be("Français, VF", "a quoted value keeps its comma");
    }

    [Fact]
    public void Sound_inside_the_qualities_stays_with_them()
    {
        var manifest = HlsPlaylist.Parse("""
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2"
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=1500000,RESOLUTION=1280x720
            mid.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS="mp4a.40.5"
            radio.m3u8
            """, Master);

        manifest.Tracks.Select(t => (t.Id, t.HasVideo, t.HasAudio)).Should().Equal(("v0", true, true), ("v1", true, true), ("a0", false, true));
        var info = manifest.ToMediaInfo();
        MediaChoices.Options(info).Select(o => o.Selection.FormatSpec).Should().Equal("stream:v1", "stream:v0", "stream:a0");
    }

    [Fact]
    public void A_media_playlist_lists_its_pieces_with_keys_ivs_and_byte_ranges()
    {
        var url = new Uri("https://cdn.example/hls/720/index.m3u8");
        var media = HlsPlaylist.ParseMedia("""
            #EXTM3U
            #EXT-X-VERSION:4
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-PLAYLIST-TYPE:VOD
            #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example/k1"
            #EXTINF:6.0,
            seg7.ts
            #EXT-X-KEY:METHOD=AES-128,URI="k2",IV=0x000102030405060708090a0b0c0d0e0f
            #EXTINF:6.0,
            seg8.ts
            #EXT-X-KEY:METHOD=NONE
            #EXT-X-BYTERANGE:1000@0
            #EXTINF:4.5,
            all.ts
            #EXT-X-BYTERANGE:500
            #EXTINF:4.5,
            all.ts
            #EXT-X-ENDLIST
            """, url);

        media.Duration.Should().Be(TimeSpan.FromSeconds(21));
        media.Pieces.Should().HaveCount(4);
        media.Pieces[0].Url.Should().Be(new Uri("https://cdn.example/hls/720/seg7.ts"));
        media.Pieces[0].Key!.Url.Should().Be(new Uri("https://keys.example/k1"));
        media.Pieces[0].Key!.Iv.Should().Equal([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7], "without an IV the sequence number is the IV");
        media.Pieces[1].Key!.Url.Should().Be(new Uri("https://cdn.example/hls/720/k2"));
        media.Pieces[1].Key!.Iv.Should().Equal(Enumerable.Range(0, 16).Select(i => (byte)i));
        (media.Pieces[2].Key, media.Pieces[2].Offset, media.Pieces[2].Length).Should().Be((null, 0L, 1000L));
        (media.Pieces[3].Offset, media.Pieces[3].Length).Should().Be((1000L, 500L), "a range without an offset continues the previous one");
    }

    [Fact]
    public void Fragmented_mp4_starts_with_its_initialization_section()
    {
        var media = HlsPlaylist.ParseMedia("""
            #EXTM3U
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:4,
            1.m4s
            #EXTINF:4,
            2.m4s
            #EXT-X-ENDLIST
            """, new Uri("https://cdn.example/v/index.m3u8"));

        media.Pieces.Select(p => (p.Url.Segments[^1], p.IsInit)).Should().Equal(("init.mp4", true), ("1.m4s", false), ("2.m4s", false));
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://fairplay\",KEYFORMAT=\"com.apple.streamingkeydelivery\"", "copy-protected")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"data:x\",KEYFORMAT=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"", "copy-protected")]
    public void Drm_is_refused_without_asking_yt_dlp(string keyLine, string reason)
    {
        var text = $"#EXTM3U\n{keyLine}\n#EXTINF:4,\n1.ts\n#EXT-X-ENDLIST\n";

        FluentActions.Invoking(() => HlsPlaylist.ParseMedia(text, Master)).Should().Throw<MediaException>().Where(e => e.Conclusive && e.Message.Contains(reason));
    }

    [Fact]
    public void A_live_broadcast_is_refused()
    {
        FluentActions.Invoking(() => HlsPlaylist.ParseMedia("#EXTM3U\n#EXTINF:4,\n1.ts\n#EXTINF:4,\n2.ts\n", Master))
            .Should().Throw<MediaException>().Where(e => e.Conclusive && e.Message.Contains("live"));
    }

    [Fact]
    public void A_media_playlist_given_directly_is_one_quality()
    {
        var manifest = HlsPlaylist.Parse("#EXTM3U\n#EXTINF:10,\na.ts\n#EXTINF:10,\nb.ts\n#EXT-X-ENDLIST\n", new Uri("https://cdn.example/v/index.m3u8"));

        manifest.Duration.Should().Be(TimeSpan.FromSeconds(20));
        manifest.Tracks.Should().ContainSingle().Which.Pieces.Should().HaveCount(2);
        MediaChoices.Options(manifest.ToMediaInfo()).Should().ContainSingle().Which.Selection.FormatSpec.Should().Be("stream:v0");
    }
}

public sealed class DashManifestTests
{
    // movie-box.co's manifest for Lupin S1E1 (2026-10-26), as ffmpeg's DASH muxer writes them.
    private const string MovieBox = """
        <?xml version="1.0" encoding="utf-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" profiles="urn:mpeg:dash:profile:isoff-live:2011" type="static"
            mediaPresentationDuration="PT48M38.7S" maxSegmentDuration="PT5.0S" minBufferTime="PT12.3S">
          <Period id="0" start="PT0.0S">
            <AdaptationSet id="0" contentType="video" frameRate="25/1" maxWidth="1920" maxHeight="1080">
              <Representation id="0" mimeType="video/mp4" codecs="hev1.1.6.L120.90" bandwidth="894063" width="1920" height="1080">
                <SegmentTemplate timescale="1000000" duration="5000000" initialization="init-stream$RepresentationID$.m4s" media="chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1" />
              </Representation>
              <Representation id="2" mimeType="video/mp4" codecs="hev1.1.6.L90.90" bandwidth="298386" width="854" height="480">
                <SegmentTemplate timescale="1000000" duration="5000000" initialization="init-stream$RepresentationID$.m4s" media="chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1" />
              </Representation>
            </AdaptationSet>
            <AdaptationSet id="1" contentType="audio" lang="und">
              <Representation id="3" mimeType="audio/mp4" codecs="mp4a.40.2" bandwidth="128000" audioSamplingRate="44100">
                <SegmentTemplate timescale="1000000" duration="5000000" initialization="init-stream$RepresentationID$.m4s" media="chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1" />
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    private static readonly Uri Url = new("https://sbcdnw.hakunaymatata.com/dash/838601903666779168_1_1_1080_h265_483/index_web.mpd");

    [Fact]
    public void A_numbered_template_lists_every_piece_of_the_film()
    {
        var manifest = DashManifest.Parse(MovieBox, Url);

        manifest.Duration.Should().Be(new TimeSpan(0, 0, 48, 38, 700));
        manifest.Tracks.Select(t => (t.Id, t.Height, t.HasVideo, t.HasAudio)).Should().Equal(("v0", 1080, true, false), ("v1", 480, true, false), ("a0", (int?)null, false, true));
        var pieces = manifest.Tracks[0].Pieces!;
        pieces.Should().HaveCount(585, "an initialization section, then 2918.7 s in pieces of 5 s");
        pieces[0].Should().Be(new StreamPiece(new Uri(Url, "init-stream0.m4s"), null, null, null, IsInit: true));
        pieces[1].Url.Should().Be(new Uri(Url, "chunk-stream0-00001.m4s"));
        pieces[^1].Url.Should().Be(new Uri(Url, "chunk-stream0-00584.m4s"), "the server has exactly that many");
        manifest.Tracks[2].Pieces![^1].Url.Should().Be(new Uri(Url, "chunk-stream3-00584.m4s"));
        manifest.Tracks[0].Fps.Should().Be(25);
        manifest.Tracks[2].Language.Should().BeNull("\"und\" says nothing");
    }

    [Fact]
    public void Each_picture_quality_pairs_with_the_sound_into_an_mp4()
    {
        var options = MediaChoices.Options(DashManifest.Parse(MovieBox, Url).ToMediaInfo());

        options.Select(o => (o.Quality, o.Codec, o.Selection.FormatSpec, o.Selection.Container)).Should().Equal(
            ("1080p", "H.265", "stream:v0+a0", "mp4"),
            ("480p", "H.265", "stream:v1+a0", "mp4"),
            ("128 kbit/s", "AAC", "stream:a0", "m4a"));
        options[0].Selection.EstimatedSize.Should().BeInRange(370_000_000, 375_000_000, "894 + 128 kbit/s for 48:38.7");
    }

    [Fact]
    public void A_timeline_counts_its_repeats_and_names_pieces_by_time()
    {
        var manifest = DashManifest.Parse("""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT20S">
              <BaseURL>https://media.example/film/</BaseURL>
              <Period>
                <AdaptationSet mimeType="video/mp4" codecs="avc1.64001f">
                  <SegmentTemplate timescale="90000" media="$RepresentationID$/t$Time$.m4s" initialization="$RepresentationID$/init.mp4">
                    <SegmentTimeline><S t="0" d="360000" r="3" /><S d="180000" r="-1" /></SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="hd" bandwidth="3000000" width="1280" height="720" />
                </AdaptationSet>
              </Period>
            </MPD>
            """, Url);

        var pieces = manifest.Tracks.Single().Pieces!;
        pieces.Skip(1).Select(p => p.Url.AbsoluteUri).Should().Equal(
            "https://media.example/film/hd/t0.m4s", "https://media.example/film/hd/t360000.m4s", "https://media.example/film/hd/t720000.m4s",
            "https://media.example/film/hd/t1080000.m4s", "https://media.example/film/hd/t1440000.m4s", "https://media.example/film/hd/t1620000.m4s");
        pieces[0].Url.AbsoluteUri.Should().Be("https://media.example/film/hd/init.mp4");
    }

    [Fact]
    public void A_segment_list_and_a_single_file_are_read_too()
    {
        var manifest = DashManifest.Parse("""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/webm">
                  <Representation id="v" bandwidth="1000" height="360">
                    <SegmentList><Initialization sourceURL="v.webm" range="0-499" /><SegmentURL media="v.webm" mediaRange="500-1999" /></SegmentList>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en">
                  <Label>English</Label>
                  <Role schemeIdUri="urn:mpeg:dash:role:2011" value="main" />
                  <Representation id="a" bandwidth="128000"><BaseURL>audio/en.m4a</BaseURL><SegmentBase indexRange="0-99" /></Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """, Url);

        var video = manifest.Tracks[0];
        (video.Ext, video.Pieces![0].Offset, video.Pieces[0].Length, video.Pieces[1].Offset, video.Pieces[1].Length).Should().Be(("webm", 0L, 500L, 500L, 1500L));
        var audio = manifest.Tracks[1];
        audio.Pieces.Should().ContainSingle().Which.Url.Should().Be(new Uri(Url, "audio/en.m4a"));
        (audio.Language, audio.Name).Should().Be(("en", "English, default"));
    }

    [Fact]
    public void Ads_in_periods_of_their_own_give_way_to_the_film()
    {
        var manifest = DashManifest.Parse("""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT100S">
              <Period id="ad" start="PT0S"><AdaptationSet contentType="video"><Representation id="ad" bandwidth="1"><SegmentTemplate media="ad-$Number$.m4s" duration="5" /></Representation></AdaptationSet></Period>
              <Period id="film" start="PT10S"><AdaptationSet contentType="video"><Representation id="film" bandwidth="1"><SegmentTemplate media="film-$Number$.m4s" duration="10" /></Representation></AdaptationSet></Period>
            </MPD>
            """, Url);

        manifest.Duration.Should().Be(TimeSpan.FromSeconds(90));
        manifest.Tracks.Single().Pieces!.Select(p => p.Url.Segments[^1]).Should().HaveCount(9).And.StartWith("film-1.m4s");
    }

    [Theory]
    [InlineData("""<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic"><Period /></MPD>""", "live")]
    [InlineData("""
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT8S"><Period><AdaptationSet contentType="video">
          <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed" />
          <Representation id="v" bandwidth="1"><SegmentTemplate media="$Number$.m4s" duration="4" /></Representation>
        </AdaptationSet></Period></MPD>
        """, "copy-protected")]
    public void Live_and_protected_streams_are_refused_at_once(string xml, string reason)
    {
        FluentActions.Invoking(() => DashManifest.Parse(xml, Url)).Should().Throw<MediaException>().Where(e => e.Conclusive && e.Message.Contains(reason));
    }

    [Fact]
    public void A_manifest_with_a_dtd_is_refused()
    {
        const string xml = """<?xml version="1.0"?><!DOCTYPE MPD [<!ENTITY x SYSTEM "file:///C:/Windows/win.ini">]><MPD>&x;</MPD>""";

        FluentActions.Invoking(() => DashManifest.Parse(xml, Url)).Should().Throw<MediaException>().WithMessage("*not valid XML*");
    }
}

public sealed class StreamSpecTests
{
    [Theory]
    [InlineData("stream:v1+a0", "v1", "a0")]
    [InlineData("stream:v12", "v12", null)]
    [InlineData("stream:a3", null, "a3")]
    public void A_spec_names_the_picture_and_sound_tracks(string spec, string? video, string? audio)
    {
        StreamSpec.TryParse(spec, out var v, out var a).Should().BeTrue();
        (v, a).Should().Be((video, audio));
    }

    [Theory]
    [InlineData("137+140")]
    [InlineData("subs:en")]
    [InlineData("stream:")]
    [InlineData("stream:a0+v1")]
    [InlineData("stream:v1+a0+a2")]
    public void Anything_else_is_not_a_stream_spec(string spec)
    {
        StreamSpec.IsSpec(spec).Should().BeFalse();
    }

    [Fact]
    public void Yt_dlp_gets_the_browsers_headers_for_a_stream_and_never_for_youtube()
    {
        var tools = new MediaTools(@"C:\OB2\tools");
        var item = new DownloadItem
        {
            Url = "https://cdn.example/dash/index_web.mpd",
            Referrer = "https://movie-box.co/",
            UserAgent = "Chrome/141",
            Headers = new(StringComparer.OrdinalIgnoreCase) { ["X-MB-Token"] = "abc", ["Origin"] = "https://movie-box.co" },
            FileName = "Lupin.mp4",
            DestPath = @"C:\Videos\Lupin.mp4",
            MediaFormat = "0+3",
            MediaContainer = "mp4",
        };

        var args = YtDlp.DownloadArguments(tools, item);
        args.Should().ContainInOrder("--user-agent", "Chrome/141");
        args.Should().ContainInOrder("--add-header", "X-MB-Token:abc");
        args.Should().ContainInOrder("--add-header", "Origin:https://movie-box.co");
        YtDlp.InfoArguments(tools, new Uri(item.Url), new Uri(item.Referrer), item.Headers, item.UserAgent).Should().ContainInOrder("--add-header", "X-MB-Token:abc");

        item.Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
        YtDlp.DownloadArguments(tools, item).Should().NotContain(["--user-agent", "--add-header"]);
        item.Url = "https://cdn.example/dash/index_web.mpd";
        item.Headers.Clear();
        YtDlp.DownloadArguments(tools, item).Should().NotContain("--user-agent", "a page from the button carries the browser's agent but no headers");
    }
}

public sealed class StreamInspectorTests : IDisposable
{
    private static readonly Uri Page = new("https://movie-box.co/movies/lupin-2rBwMMWNWZ");
    private static readonly Uri Manifest = new("https://cdn.example/dash/index_web.mpd");
    private readonly TempDirectory _temp = new();
    private readonly FakeServer _server = new();
    private readonly ScriptedYtDlp _ytDlp = new();
    private readonly MediaInspector _inspector;

    public StreamInspectorTests()
    {
        var tools = new MediaTools(_temp.Combine("tools"), _ytDlp);
        Directory.CreateDirectory(tools.FfmpegFolder);
        foreach (var file in new[] { tools.YtDlpPath, tools.DenoPath, tools.FfmpegPath })
        {
            File.WriteAllText(file, string.Empty);
        }

        _inspector = new MediaInspector(tools, null, new StreamManifestReader(new HttpClient(_server)));
    }

    public void Dispose()
    {
        _server.Dispose();
        _temp.Dispose();
    }

    private static StreamRequest AsTheBrowserAsked => new(
        Manifest, "https://movie-box.co/", "Chrome/141", "edge=1", new Dictionary<string, string> { ["X-MB-Token"] = "abc", ["Origin"] = "https://movie-box.co" });

    [Fact]
    public async Task OB2_reads_the_stream_itself_with_the_browsers_request_and_yt_dlp_is_not_asked()
    {
        _ytDlp.Answers[Page.AbsoluteUri] = (1, "ERROR: Unsupported URL: " + Page.AbsoluteUri);
        _server.Respond = request => request.Headers.TryGetValues("X-MB-Token", out var token) && token.Single() == "abc"
            ? Text(DashManifestTestsMpd)
            : new HttpResponseMessage(HttpStatusCode.Forbidden);

        var (source, info) = await _inspector.InspectPageAsync(Page, [AsTheBrowserAsked], "Lupin - MovieBox");

        source.Should().Be(Manifest);
        info.Title.Should().Be("Lupin - MovieBox");
        info.FromStream.Should().BeTrue();
        MediaChoices.Options(info)[0].Selection.FormatSpec.Should().Be("stream:v0+a0");
        var sent = _server.Requests.Single();
        sent.Headers.GetValues("Origin").Should().Equal("https://movie-box.co");
        sent.Headers.GetValues("Cookie").Should().Equal("edge=1");
        sent.Headers.Referrer.Should().Be(new Uri("https://movie-box.co/"));
        sent.Headers.UserAgent.ToString().Should().Be("Chrome/141");
        _ytDlp.Runs.Should().ContainSingle("only the page went to yt-dlp");
    }

    [Fact]
    public async Task When_OB2_cannot_read_the_stream_yt_dlp_tries_with_the_same_headers_and_otherwise_OB2_says_why()
    {
        _server.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        _ytDlp.Answers[Manifest.AbsoluteUri] = (0, """{ "title": "index_web", "extractor_key": "Generic", "format_id": "0", "ext": "mp4", "height": 1080 }""");

        var info = await _inspector.InspectStreamAsync(AsTheBrowserAsked);

        info.FromStream.Should().BeFalse("yt-dlp read it, so yt-dlp downloads it");
        _ytDlp.Runs.Single().Should().ContainInOrder("--add-header", "X-MB-Token:abc").And.NotContain(a => a.Contains("edge=1"), "cookies stay off the command line");

        _ytDlp.Answers[Manifest.AbsoluteUri] = (1, "ERROR: [generic] index_web: HTTP Error 403: Forbidden");
        var other = AsTheBrowserAsked with { Url = new Uri("https://cdn.example/dash/other.mpd") };
        _ytDlp.Answers[other.Url.AbsoluteUri] = (1, "ERROR: [generic] other: HTTP Error 403: Forbidden");
        await FluentActions.Awaiting(() => _inspector.InspectStreamAsync(other)).Should().ThrowAsync<MediaException>()
            .WithMessage("The stream's server refused it: The server answered 403 Forbidden.");
    }

    [Fact]
    public async Task A_protected_stream_is_refused_without_asking_yt_dlp()
    {
        _server.Respond = _ => Text("""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT8S"><Period><AdaptationSet contentType="video">
              <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed" />
              <Representation id="v" bandwidth="1"><SegmentTemplate media="$Number$.m4s" duration="4" /></Representation>
            </AdaptationSet></Period></MPD>
            """);

        await FluentActions.Awaiting(() => _inspector.InspectPageAsync(Page, [AsTheBrowserAsked, AsTheBrowserAsked with { Url = new Uri("https://cdn.example/b.mpd") }], null))
            .Should().ThrowAsync<MediaException>().WithMessage("*copy-protected*");
        _ytDlp.Runs.Should().ContainSingle("the page; neither stream");
    }

    private const string DashManifestTestsMpd = """
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S"><Period>
          <AdaptationSet contentType="video"><Representation id="0" mimeType="video/mp4" codecs="hev1.1.6.L120.90" bandwidth="894063" width="1920" height="1080">
            <SegmentTemplate timescale="1000000" duration="5000000" initialization="init-stream$RepresentationID$.m4s" media="chunk-stream$RepresentationID$-$Number%05d$.m4s" /></Representation></AdaptationSet>
          <AdaptationSet contentType="audio"><Representation id="3" mimeType="audio/mp4" codecs="mp4a.40.2" bandwidth="128000">
            <SegmentTemplate timescale="1000000" duration="5000000" initialization="init-stream$RepresentationID$.m4s" media="chunk-stream$RepresentationID$-$Number%05d$.m4s" /></Representation></AdaptationSet>
        </Period></MPD>
        """;

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/dash+xml") };

    private sealed class FakeServer : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = Respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class ScriptedYtDlp : IProcessRunner
    {
        public Dictionary<string, (int Code, string Line)> Answers { get; } = new(StringComparer.Ordinal);

        public List<IReadOnlyList<string>> Runs { get; } = [];

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
        {
            Runs.Add(arguments);
            var (code, line) = Answers.TryGetValue(arguments[^1], out var answer) ? answer : (1, "ERROR: Unsupported URL: " + arguments[^1]);
            (code == 0 ? onOutput : onError)(line);
            return Task.FromResult(code);
        }
    }
}
