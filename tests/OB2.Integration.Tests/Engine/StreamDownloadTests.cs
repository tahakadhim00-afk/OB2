using System.Diagnostics;
using System.Text.Json;
using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Http;
using OB2.Core.Media;
using OB2.Integration.Tests.Infrastructure;
using OB2.Tests.Common;

namespace OB2.Integration.Tests.Engine;

/// <summary>
/// D-80: OB2 downloads HLS and DASH streams itself, without yt-dlp. Real ffmpeg writes a short
/// stream in each form (HLS encrypted with AES-128, DASH named like movie-box.co's), the test
/// server demands a token header as those players' servers do, and OB2's engine downloads, decrypts
/// and joins it. ffprobe checks the result.
/// </summary>
public sealed class StreamDownloadTests : IClassFixture<StreamDownloadTests.Streams>, IAsyncLifetime
{
    private const string Token = "X-Player-Token";
    private readonly Streams _streams;
    private EngineFixture _fx = null!;
    private TestHttpServer _server = null!;

    public StreamDownloadTests(Streams streams)
    {
        _streams = streams;
    }

    public async Task InitializeAsync()
    {
        _fx = new EngineFixture(EngineFixture.FastOptions with { Connections = 4 });
        if (FfmpegFactAttribute.Folder is { } ffmpeg)
        {
            _fx.MediaTools = new MediaTools(Path.GetDirectoryName(ffmpeg)!);
            _fx.Rebuild();
        }

        _server = await TestHttpServer.StartAsync();
        _server.StaticFolder = _streams.Folder;
        _server.StaticHeader = (Token, "secret");
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _fx.DisposeAsync();
    }

    [FfmpegFact]
    public async Task An_encrypted_hls_stream_downloads_with_the_players_token_and_plays()
    {
        var done = await DownloadAsync("hls/index.m3u8", "stream:v0", "mp4");

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        Probe(done.DestPath).Should().BeEquivalentTo(new[] { "video", "audio" });
        Duration(done.DestPath).Should().BeInRange(5.5, 6.5);
        _server.Requests.Count(r => r.PathAndQuery.EndsWith("key.bin", StringComparison.Ordinal)).Should().Be(1, "one key for every piece");
        _server.Requests.Should().OnlyContain(r => r.Header(Token) == "secret" && r.Header("Referer") == "https://player.example/watch");
        Directory.Exists(done.MediaPartsFolder).Should().BeFalse();
    }

    [FfmpegFact]
    public async Task A_dash_stream_joins_its_picture_and_sound_into_one_mp4()
    {
        var done = await DownloadAsync("dash/manifest.mpd", "stream:v0+a0", "mp4");

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        Probe(done.DestPath).Should().BeEquivalentTo(new[] { "video", "audio" });
        Duration(done.DestPath).Should().BeInRange(5.5, 6.5);
        done.TotalBytes.Should().Be(new FileInfo(done.DestPath).Length);
    }

    [FfmpegFact]
    public async Task Pausing_keeps_the_finished_pieces_and_resuming_fetches_only_the_rest()
    {
        _server.StaticDelay = TimeSpan.FromMilliseconds(250);
        var item = await AddAsync("dash/manifest.mpd", "stream:v0+a0", "mp4");
        _fx.Engine.Start(item.Id).Should().BeTrue();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((!Directory.Exists(item.MediaPartsFolder) || Directory.GetFiles(item.MediaPartsFolder, "*.part").Length < 4) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await _fx.Engine.PauseAsync(item.Id);
        var paused = _fx.Repository.Get(item.Id)!;
        paused.Status.Should().Be(DownloadStatus.Paused);
        var kept = Directory.GetFiles(paused.MediaPartsFolder, "*.part").Select(Path.GetFileName).ToList();
        kept.Should().HaveCountGreaterThanOrEqualTo(4);
        paused.DownloadedBytes.Should().BePositive();
        var before = _server.Requests.Count;

        _server.StaticDelay = TimeSpan.Zero;
        var done = await _fx.RunAsync(item.Id);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        var fetchedAgain = _server.Requests.Skip(before).Select(r => r.PathAndQuery).ToList();
        fetchedAgain.Should().OnlyHaveUniqueItems().And.HaveCount(_streams.DashPieces + 1 - kept.Count, "the manifest again, and only the pieces not kept");
        _fx.Repository.GetHistory(item.Id).Select(e => e.EventType).Should().Contain(["paused", "resumed", "completed"]);
    }

    [FfmpegFact]
    public async Task An_expired_token_fails_with_the_reason_and_what_to_do()
    {
        _server.StaticHeader = (Token, "a newer token");

        var done = await DownloadAsync("hls/index.m3u8", "stream:v0", "mp4");

        done.Status.Should().Be(DownloadStatus.Failed);
        done.ErrorCode.Should().Be(ErrorCodes.Http(403));
        done.ErrorMessage.Should().Contain("403").And.Contain("expired").And.Contain("browser");
    }

    private async Task<DownloadItem> DownloadAsync(string path, string format, string container) =>
        await _fx.RunAsync((await AddAsync(path, format, container)).Id);

    /// <summary>As the Download Video window or the browser's button adds it: no probe, the browser's request.</summary>
    private Task<DownloadItem> AddAsync(string path, string format, string container)
    {
        var url = _server.Url("/static/" + path);
        var name = "Clip." + container;
        return _fx.Engine.AddAsync(
            new DownloadRequest
            {
                Url = url,
                SaveFolder = _fx.SaveFolder,
                FileName = name,
                Referrer = "https://player.example/watch",
                UserAgent = "Chrome/141",
                Headers = new Dictionary<string, string> { [Token] = "secret", ["Origin"] = "https://player.example" },
                MediaFormat = format,
                MediaContainer = container,
            },
            new ProbeResult(url, null, false, name, null, null, null));
    }

    private static string[] Probe(string file) =>
        [.. FfprobeJson(file, "-show_entries", "stream=codec_type").GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()!)];

    private static double Duration(string file) =>
        double.Parse(FfprobeJson(file, "-show_entries", "format=duration").GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private static JsonElement FfprobeJson(string file, params string[] query)
    {
        var info = new ProcessStartInfo(Path.Combine(FfmpegFactAttribute.Folder!, "ffprobe.exe")) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in (string[])["-v", "error", .. query, "-of", "json", file])
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var json = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>Six seconds of test picture and tone, written once by ffmpeg as HLS (AES-128) and as DASH.</summary>
    public sealed class Streams : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public Streams()
        {
            Folder = _temp.Path;
            if (FfmpegFactAttribute.Folder is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.Combine(Folder, "hls"));
            Directory.CreateDirectory(Path.Combine(Folder, "dash"));
            var key = Path.Combine(Folder, "hls", "key.bin");
            File.WriteAllBytes(key, Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray());
            // Without an IV line ffmpeg uses each piece's sequence number, the case players compute.
            var keyInfo = Path.Combine(Folder, "keyinfo.txt");
            File.WriteAllLines(keyInfo, ["key.bin", key]);

            string[] source = ["-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "25", "-c:a", "aac", "-shortest"];
            Ffmpeg([.. source, "-f", "hls", "-hls_time", "1", "-hls_playlist_type", "vod", "-hls_key_info_file", keyInfo,
                "-hls_segment_filename", Path.Combine(Folder, "hls", "seg%03d.ts"), Path.Combine(Folder, "hls", "index.m3u8")]);
            // From inside the folder: on Windows the DASH muxer finds a manifest's folder only after
            // a '/', so with a backslash path it writes the pieces into the current folder instead.
            Ffmpeg([.. source, "-map", "0:v", "-map", "1:a", "-f", "dash", "-seg_duration", "1", "-use_template", "1", "-use_timeline", "0",
                "-init_seg_name", "init-stream$RepresentationID$.m4s", "-media_seg_name", "chunk-stream$RepresentationID$-$Number%05d$.m4s",
                "manifest.mpd"], Path.Combine(Folder, "dash"));
            Directory.GetFiles(Path.Combine(Folder, "dash"), "*.m4s").Should().HaveCountGreaterThan(4, "ffmpeg wrote the pieces beside the manifest");
            // As many as the manifest lists: ffmpeg may also write a sliver of sound past the length it declares.
            var manifest = DashManifest.Parse(File.ReadAllText(Path.Combine(Folder, "dash", "manifest.mpd")), new Uri("https://x.example/dash/manifest.mpd"));
            DashPieces = manifest.Tracks.Sum(t => t.Pieces!.Count);
        }

        public string Folder { get; }

        /// <summary>Initialization sections and pieces of both DASH tracks, as the manifest lists them.</summary>
        public int DashPieces { get; }

        public void Dispose() => _temp.Dispose();

        private static void Ffmpeg(string[] args, string? workingDirectory = null)
        {
            var info = new ProcessStartInfo(Path.Combine(FfmpegFactAttribute.Folder!, "ffmpeg.exe"))
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            };
            foreach (var arg in (string[])["-hide_banner", "-loglevel", "error", "-y", .. args])
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, errors);
        }
    }
}
