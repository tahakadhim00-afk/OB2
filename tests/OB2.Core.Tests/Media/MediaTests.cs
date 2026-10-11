using OB2.Core.Downloads;
using OB2.Core.Media;
using OB2.Tests.Common;

namespace OB2.Core.Tests.Media;

/// <summary>Decision D-71: video pages through yt-dlp. Everything here runs without yt-dlp.</summary>
public sealed class VideoSitesTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1&t=42s", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/aBcDeFgHiJ0", "https://www.youtube.com/watch?v=aBcDeFgHiJ0")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?autoplay=1", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&feature=share", "https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void YouTube_video_addresses_become_one_watch_address(string input, string expected)
    {
        VideoSites.YouTubeVideo(new Uri(input)).Should().Be(new Uri(expected));
    }

    [Theory]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/@channel/videos")]
    [InlineData("https://www.youtube.com/playlist?list=PL1")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://example.com/video.mp4")]
    public void Other_addresses_are_not_YouTube_videos(string input)
    {
        VideoSites.YouTubeVideo(new Uri(input)).Should().BeNull();
    }
}

public sealed class MediaChoicesTests
{
    // Trimmed from a real `yt-dlp -J` answer for a YouTube video.
    private const string YouTubeJson = """
        {
          "title": "Launch day / highlights", "extractor_key": "Youtube", "duration": 212,
          "formats": [
            { "format_id": "sb0", "ext": "mhtml", "vcodec": "none", "acodec": "none", "format_note": "storyboard" },
            { "format_id": "251", "ext": "webm", "vcodec": "none", "acodec": "opus", "abr": 130.5, "filesize": 3500000, "format_note": "English original (default), medium", "language": "en" },
            { "format_id": "140", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 129.5, "filesize": 3400000, "format_note": "English original (default), medium", "language": "en" },
            { "format_id": "140-1", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 129.4, "filesize": 3400000, "format_note": "Spanish, medium", "language": "es" },
            { "format_id": "18", "ext": "mp4", "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "height": 360, "width": 640, "fps": 25, "tbr": 500, "filesize_approx": 13000000, "format_note": "360p" },
            { "format_id": "136", "ext": "mp4", "vcodec": "avc1.4d401f", "acodec": "none", "height": 720, "width": 1280, "fps": 25, "tbr": 1500, "filesize": 40000000 },
            { "format_id": "247", "ext": "webm", "vcodec": "vp9", "acodec": "none", "height": 720, "width": 1280, "fps": 25, "tbr": 1400, "filesize": 38000000 },
            { "format_id": "137", "ext": "mp4", "vcodec": "avc1.640028", "acodec": "none", "height": 1080, "width": 1920, "fps": 25, "tbr": 4000, "filesize": 105000000 },
            { "format_id": "248", "ext": "webm", "vcodec": "vp9", "acodec": "none", "height": 1080, "width": 1920, "fps": 25, "tbr": 2700, "filesize": 70000000 },
            { "format_id": "299", "ext": "mp4", "vcodec": "avc1.64002a", "acodec": "none", "height": 1080, "width": 1920, "fps": 50, "tbr": 6000, "filesize": 160000000 },
            { "format_id": "313", "ext": "webm", "vcodec": "vp9", "acodec": "none", "height": 2160, "width": 3840, "fps": 25, "tbr": 18000, "filesize": 480000000 }
          ]
        }
        """;

    private static readonly MediaInfo Info = MediaInfo.Parse(YouTubeJson);

    [Fact]
    public void Reads_title_length_and_formats_without_storyboards()
    {
        Info.Title.Should().Be("Launch day / highlights");
        Info.Duration.Should().Be(TimeSpan.FromSeconds(212));
        Info.Formats.Select(f => f.Id).Should().NotContain("sb0");
    }

    [Fact]
    public void Lists_one_entry_per_quality_best_first_preferring_mp4_at_equal_quality()
    {
        var videos = MediaChoices.Videos(Info);

        videos.Select(v => v.Quality).Should().Equal("2160p", "1080p50", "1080p", "720p", "360p");
        videos.Select(v => v.Id).Should().Equal("313", "299", "137", "136", "18");
        videos[0].VideoCodecName.Should().Be("VP9");
    }

    [Fact]
    public void Lists_audio_tracks_with_the_default_one_first()
    {
        var audios = MediaChoices.Audios(Info);

        audios.Select(a => a.Id).Should().Equal("251", "140", "140-1");
        audios[2].Language.Should().Be("es");
    }

    [Fact]
    public void Picks_audio_that_merges_into_the_same_file_type()
    {
        var audios = MediaChoices.Audios(Info);
        var byId = Info.Formats.ToDictionary(f => f.Id);

        MediaChoices.AudioFor(byId["137"], audios)!.Id.Should().Be("140");
        MediaChoices.AudioFor(byId["313"], audios)!.Id.Should().Be("251");
    }

    [Fact]
    public void Turns_a_choice_into_a_format_selector_container_and_size()
    {
        var f = Info.Formats.ToDictionary(x => x.Id);

        MediaChoices.Select(f["137"], f["140"]).Should().Be(new MediaSelection("137+140", "mp4", 108_400_000));
        MediaChoices.Select(f["313"], f["251"]).Should().Be(new MediaSelection("313+251", "webm", 483_500_000));
        MediaChoices.Select(f["248"], f["140"]).Container.Should().Be("mkv");
        MediaChoices.Select(f["18"], f["140"]).Should().Be(new MediaSelection("18", "mp4", 13_000_000)); // has its own sound
        MediaChoices.Select(null, f["140-1"]).Should().Be(new MediaSelection("140-1", "m4a", 3_400_000)); // audio only
    }

    [Fact]
    public void YouTubes_hls_picture_and_sound_merge_into_mp4()
    {
        // D-73: HLS sound is "mp4" (raw AAC), HLS picture an MPEG-TS stream; ffmpeg can't put those in MKV.
        var info = MediaInfo.Parse("""
            { "title": "Clip", "formats": [
              { "format_id": "233", "ext": "mp4", "vcodec": "none", "acodec": "mp4a.40.5", "format_note": "Default, low", "language": "ar" },
              { "format_id": "251", "ext": "webm", "vcodec": "none", "acodec": "opus", "abr": 130, "format_note": "Default, medium", "language": "ar" },
              { "format_id": "269", "ext": "mp4", "vcodec": "avc1.4D400C", "acodec": "none", "height": 144, "protocol": "m3u8_native" } ] }
            """);
        var f = info.Formats.ToDictionary(x => x.Id);

        MediaChoices.Select(f["269"], f["233"]).Container.Should().Be("mp4");
        MediaChoices.AudioFor(f["269"], MediaChoices.Audios(info))!.Id.Should().Be("233");
        MediaChoices.Options(info)[0].Selection.Should().Be(new MediaSelection("269+233", "mp4", null));
    }

    [Fact]
    public void A_site_with_a_single_file_reports_it_on_the_root()
    {
        var info = MediaInfo.Parse("""{ "title": "Clip", "format_id": "hls-720", "ext": "mp4", "height": 720 }""");

        info.Formats.Should().ContainSingle().Which.Quality.Should().Be("720p");
        MediaChoices.Select(MediaChoices.Videos(info)[0], null).FormatSpec.Should().Be("hls-720");
        MediaChoices.Options(info).Should().ContainSingle().Which.Should().Be(new MediaOption(new MediaSelection("hls-720", "mp4", null), false, "720p", null, null));
    }

    [Fact]
    public void The_button_offers_every_quality_with_the_default_sound_then_sound_alone_per_language()
    {
        var options = MediaChoices.Options(Info);

        options.Should().Equal(
            new MediaOption(new MediaSelection("313+251", "webm", 483_500_000), false, "2160p", "VP9", null),
            new MediaOption(new MediaSelection("299+140", "mp4", 163_400_000), false, "1080p50", "H.264", null),
            new MediaOption(new MediaSelection("137+140", "mp4", 108_400_000), false, "1080p", "H.264", null),
            new MediaOption(new MediaSelection("136+140", "mp4", 43_400_000), false, "720p", "H.264", null),
            new MediaOption(new MediaSelection("18", "mp4", 13_000_000), false, "360p", "H.264", null),
            new MediaOption(new MediaSelection("140", "m4a", 3_400_000), true, "130 kbit/s", "AAC", "English original (default)"),
            new MediaOption(new MediaSelection("140-1", "m4a", 3_400_000), true, "129 kbit/s", "AAC", "Spanish"));
    }

    [Fact]
    public void With_one_language_the_sound_alone_entry_has_no_track_name()
    {
        var info = MediaInfo.Parse("""
            { "title": "Song", "formats": [
              { "format_id": "251", "ext": "webm", "vcodec": "none", "acodec": "opus", "abr": 130, "format_note": "medium", "language": "en" },
              { "format_id": "18", "ext": "mp4", "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "height": 360 } ] }
            """);

        MediaChoices.Options(info).Should().Equal(
            new MediaOption(new MediaSelection("18", "mp4", null), false, "360p", "H.264", null),
            new MediaOption(new MediaSelection("251", "webm", null), true, "130 kbit/s", "Opus", null));
    }

    [Fact]
    public void A_playlist_or_garbage_is_explained()
    {
        FluentActions.Invoking(() => MediaInfo.Parse("""{ "_type": "playlist", "entries": [] }"""))
            .Should().Throw<MediaException>().WithMessage("*playlist*");
        FluentActions.Invoking(() => MediaInfo.Parse("<html>")).Should().Throw<MediaException>();
    }
}

public sealed class MediaInspectorTests : IDisposable
{
    private static readonly Uri Page = new("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
    private readonly TempDirectory _temp = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly CountingYtDlp _ytDlp = new();
    private readonly MediaInspector _inspector;

    public MediaInspectorTests()
    {
        var tools = new MediaTools(_temp.Combine("tools"), _ytDlp);
        Directory.CreateDirectory(tools.FfmpegFolder);
        foreach (var file in new[] { tools.YtDlpPath, tools.DenoPath, tools.FfmpegPath })
        {
            File.WriteAllText(file, string.Empty);
        }

        _inspector = new MediaInspector(tools, _clock);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task The_list_the_button_showed_is_reused_by_the_download_for_ten_minutes()
    {
        (await _inspector.InspectAsync(Page)).Title.Should().Be("Clip");
        _clock.Advance(TimeSpan.FromMinutes(9));
        await _inspector.InspectAsync(Page);
        _ytDlp.Calls.Should().Be(1);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await _inspector.InspectAsync(Page);
        _ytDlp.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_failure_is_not_kept()
    {
        _ytDlp.Fail = true;
        await FluentActions.Awaiting(() => _inspector.InspectAsync(Page)).Should().ThrowAsync<MediaException>().WithMessage("Video unavailable");

        _ytDlp.Fail = false;
        (await _inspector.InspectAsync(Page)).Formats.Should().ContainSingle();
        _ytDlp.Calls.Should().Be(2);
    }

    private sealed class CountingYtDlp : IProcessRunner
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail)
            {
                onError("ERROR: [youtube] dQw4w9WgXcQ: Video unavailable");
                return Task.FromResult(1);
            }

            onOutput("""{ "title": "Clip", "format_id": "18", "ext": "mp4", "height": 360 }""");
            return Task.FromResult(0);
        }
    }
}

public sealed class YtDlpTests
{
    private static readonly MediaTools Tools = new(@"C:\OB2\tools");

    [Fact]
    public void Reads_progress_lines_including_unknown_values()
    {
        YtDlp.ParseProgress("[ob2] 137|downloading|1048576|105000000|NA|2500000.5")
            .Should().Be(new YtDlpProgress("137", "downloading", 1_048_576, 105_000_000, 2_500_000.5));
        YtDlp.ParseProgress("[ob2] 140|downloading|2048|NA|3400000.0|NA")
            .Should().Be(new YtDlpProgress("140", "downloading", 2048, 3_400_000, null));
        YtDlp.ParseProgress("[download] 10% of 5MiB").Should().BeNull();
        YtDlp.ParseFile(@"[ob2-file] C:\Videos\Clip.mp4").Should().Be(@"C:\Videos\Clip.mp4");
    }

    [Fact]
    public void Error_messages_lose_the_prefix_and_the_site_tag()
    {
        string[] stderr = ["WARNING: [youtube] something", "ERROR: [youtube] dQw4w9WgXcQ: Video unavailable. This video is private"];

        YtDlp.ErrorMessage(stderr).Should().Be("Video unavailable. This video is private");
        YtDlp.ErrorMessage(["ERROR: Unsupported URL: https://example.com/"]).Should().Be("Unsupported URL: https://example.com/");
        YtDlp.ErrorMessage(["nothing"]).Should().BeNull();
    }

    [Fact]
    public void Download_arguments_keep_parts_apart_and_merge_only_pairs()
    {
        var item = new DownloadItem
        {
            Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            FileName = "100% fun.mp4",
            DestPath = @"C:\Videos\100% fun.mp4",
            MediaFormat = "137+140",
            MediaContainer = "mp4",
            SpeedLimitBps = 500_000,
        };

        var args = YtDlp.DownloadArguments(Tools, item);

        args.Should().ContainInOrder("-f", "137+140");
        args.Should().ContainInOrder("--merge-output-format", "mp4");
        args.Should().ContainInOrder("-P", @"home:C:\Videos");
        args.Should().ContainInOrder("-P", @"temp:C:\Videos\100% fun.mp4.parts");
        args.Should().ContainInOrder("-o", "100%% fun.%(ext)s");
        args.Should().ContainInOrder("--limit-rate", "500000");
        args.Should().ContainInOrder("--js-runtimes", @"deno:C:\OB2\tools\deno.exe");
        args.Should().Contain("--ignore-config");
        args.Should().ContainInOrder("--encoding", "utf-8"); // D-73: file names in any script come back whole
        YtDlp.InfoArguments(Tools, new Uri(item.Url)).Should().ContainInOrder("--encoding", "utf-8");
        args.TakeLast(2).Should().Equal("--", item.Url);

        item.MediaFormat = "140";
        YtDlp.DownloadArguments(Tools, item).Should().NotContain("--merge-output-format");
    }

    [Fact]
    public void Finds_checksums_in_every_release_format()
    {
        var sha = new string('a', 64);
        var other = new string('b', 64);

        MediaToolInstaller.FindSha256($"{other}  yt-dlp_x86.exe\n{sha}  yt-dlp.exe\n", "yt-dlp.exe").Should().Be(sha);
        MediaToolInstaller.FindSha256($"{other} *ffmpeg-master-latest-win64-gpl.zip\n{sha} *ffmpeg-master-latest-win64-gpl-shared.zip", "ffmpeg-master-latest-win64-gpl-shared.zip").Should().Be(sha);
        // One checksum per file, in PowerShell's Get-FileHash layout.
        MediaToolInstaller.FindSha256($"Algorithm : SHA256\nHash      : {sha.ToUpperInvariant()}\nPath      : D:\\a\\deno.zip", "deno-x86_64-pc-windows-msvc.zip").Should().Be(sha);
        MediaToolInstaller.FindSha256($"{other}  a.exe\n{sha}  b.exe", "c.exe").Should().BeNull();
    }
}
