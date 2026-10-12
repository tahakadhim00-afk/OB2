using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Http;
using OB2.Core.Media;
using OB2.Integration.Tests.Infrastructure;

namespace OB2.Integration.Tests.Engine;

/// <summary>
/// Decision D-71: a video page downloads through yt-dlp inside the engine, with progress, pause
/// and resume, errors, and clean-up. A fake yt-dlp stands in, printing what the real one prints.
/// </summary>
public sealed class MediaDownloadTests : IAsyncLifetime
{
    private static readonly Uri Page = new("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
    private readonly FakeYtDlp _ytDlp = new();
    private EngineFixture _fx = null!;

    public Task InitializeAsync()
    {
        _fx = new EngineFixture();
        var tools = new MediaTools(Path.Combine(_fx.Root, "tools"), _ytDlp);
        Directory.CreateDirectory(tools.FfmpegFolder);
        foreach (var file in new[] { tools.YtDlpPath, tools.DenoPath, tools.FfmpegPath })
        {
            File.WriteAllText(file, string.Empty);
        }

        _fx.MediaTools = tools;
        _fx.Rebuild();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _fx.DisposeAsync();

    [Fact]
    public async Task Downloads_the_chosen_formats_and_records_the_merged_file()
    {
        var item = await AddAsync();

        var done = await _fx.RunAsync(item.Id);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        done.DestPath.Should().Be(Path.Combine(_fx.SaveFolder, "Clip.mp4"));
        File.ReadAllBytes(done.DestPath).Should().HaveCount(FakeYtDlp.FileSize);
        done.TotalBytes.Should().Be(FakeYtDlp.FileSize);
        Directory.Exists(done.MediaPartsFolder).Should().BeFalse();
        _ytDlp.Calls[0].Should().ContainInOrder("-f", "137+140").And.ContainInOrder("--merge-output-format", "mp4");
        _fx.Repository.GetHistory(item.Id).Select(e => e.EventType).Should().Contain(["started", "completed"]);
    }

    [Fact]
    public async Task A_garbled_printed_path_falls_back_to_the_name_OB2_asked_for()
    {
        // D-73: yt-dlp once dropped every Arabic letter from the path it printed.
        _ytDlp.GarblePrintedPath = true;
        var item = await AddAsync();

        var done = await _fx.RunAsync(item.Id);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        done.DestPath.Should().Be(Path.Combine(_fx.SaveFolder, "Clip.mp4"));
    }

    [Fact]
    public async Task Pausing_stops_yt_dlp_with_progress_saved_and_resuming_continues()
    {
        _ytDlp.HangAfterProgress = true;
        var item = await AddAsync();
        _fx.Engine.Start(item.Id).Should().BeTrue();
        await _ytDlp.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var progress = _fx.Engine.GetProgress(item.Id)!;
        progress.DownloadedBytes.Should().Be(1000);
        progress.TotalBytes.Should().Be(5000); // the estimate until yt-dlp reports more

        await _fx.Engine.PauseAsync(item.Id);
        var paused = _fx.Repository.Get(item.Id)!;
        paused.Status.Should().Be(DownloadStatus.Paused);
        paused.DownloadedBytes.Should().Be(1000);
        Directory.Exists(paused.MediaPartsFolder).Should().BeTrue("yt-dlp continues its parts on the next run");

        _ytDlp.HangAfterProgress = false;
        var done = await _fx.RunAsync(item.Id);
        done.Status.Should().Be(DownloadStatus.Completed);
        _fx.Repository.GetHistory(item.Id).Select(e => e.EventType).Should().Contain("resumed");
    }

    [Fact]
    public async Task A_site_error_fails_the_download_with_yt_dlps_reason()
    {
        _ytDlp.Error = "ERROR: [youtube] dQw4w9WgXcQ: Video unavailable";
        var item = await AddAsync();

        var done = await _fx.RunAsync(item.Id);

        done.Status.Should().Be(DownloadStatus.Failed);
        done.ErrorCode.Should().Be(ErrorCodes.SiteError);
        done.ErrorMessage.Should().Be("Video unavailable");
    }

    [Fact]
    public async Task Without_the_tools_the_download_says_what_is_missing()
    {
        File.Delete(_fx.MediaTools!.DenoPath);
        var item = await AddAsync();

        var done = await _fx.RunAsync(item.Id);

        done.ErrorCode.Should().Be(ErrorCodes.ToolsMissing);
        _ytDlp.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_removes_the_parts()
    {
        _ytDlp.HangAfterProgress = true;
        var item = await AddAsync();
        _fx.Engine.Start(item.Id);
        await _ytDlp.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await _fx.Engine.DeleteAsync(item.Id, deleteFile: true);

        Directory.Exists(item.MediaPartsFolder).Should().BeFalse();
        _fx.Repository.Get(item.Id).Should().BeNull();
    }

    [Fact]
    public async Task Subtitles_are_saved_under_the_name_OB2_chose()
    {
        // D-74: yt-dlp writes them into the parts folder under its own name.
        var item = await AddSubtitlesAsync();

        var done = await _fx.RunAsync(item.Id);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        done.DestPath.Should().Be(Path.Combine(_fx.SaveFolder, "Clip.en.srt"));
        File.ReadAllText(done.DestPath).Should().Be(FakeYtDlp.SubtitleText);
        done.TotalBytes.Should().Be(FakeYtDlp.SubtitleText.Length);
        Directory.Exists(done.MediaPartsFolder).Should().BeFalse();
        _ytDlp.Calls[0].Should().Contain(["--skip-download", "--write-subs"]).And.ContainInOrder("--convert-subs", "srt");
    }

    [Fact]
    public async Task Subtitles_take_a_free_name_if_a_file_took_theirs_meanwhile()
    {
        var item = await AddSubtitlesAsync();
        File.WriteAllText(item.DestPath, "the user's own");

        var done = await _fx.RunAsync(item.Id);

        done.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        done.FileName.Should().Be("Clip.en (1).srt");
        File.ReadAllText(item.DestPath).Should().Be("the user's own");
    }

    [Fact]
    public async Task A_track_yt_dlp_did_not_save_fails_with_a_reason()
    {
        _ytDlp.SaveNoSubtitles = true; // "There are no subtitles for the requested languages", exit 0
        var item = await AddSubtitlesAsync();

        var done = await _fx.RunAsync(item.Id);

        done.Status.Should().Be(DownloadStatus.Failed);
        done.ErrorCode.Should().Be(ErrorCodes.SiteError);
        done.ErrorMessage.Should().Contain("no subtitles");
    }

    private Task<DownloadItem> AddAsync() =>
        _fx.Engine.AddAsync(
            new DownloadRequest { Url = Page, SaveFolder = _fx.SaveFolder, FileName = "Clip.mp4", MediaFormat = "137+140", MediaContainer = "mp4" },
            new ProbeResult(Page, 5000, false, "Clip.mp4", null, null, null));

    private Task<DownloadItem> AddSubtitlesAsync() =>
        _fx.Engine.AddAsync(
            new DownloadRequest { Url = Page, SaveFolder = _fx.SaveFolder, FileName = "Clip.en.srt", MediaFormat = "subs:en", MediaContainer = "srt" },
            new ProbeResult(Page, null, false, "Clip.en.srt", null, null, null));

    /// <summary>
    /// Prints yt-dlp's progress and file lines; writes a part, then the merged file. With
    /// <c>--skip-download</c>, writes the converted subtitles to its home folder and prints no path.
    /// </summary>
    private sealed class FakeYtDlp : IProcessRunner
    {
        public const int FileSize = 4321;
        public const string SubtitleText = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n";

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HangAfterProgress { get; set; }

        public string? Error { get; set; }

        public bool GarblePrintedPath { get; set; }

        public bool SaveNoSubtitles { get; set; }

        public async Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            var home = Value(arguments, "home:");
            var temp = Value(arguments, "temp:");
            var stem = arguments[IndexAfter(arguments, "-o")].Replace(".%(ext)s", string.Empty, StringComparison.Ordinal);

            if (Error is not null)
            {
                onError(Error);
                return 1;
            }

            if (arguments.Contains("--skip-download"))
            {
                if (!SaveNoSubtitles)
                {
                    var name = $"{stem["subtitle:".Length..]}.{arguments[IndexAfter(arguments, "--sub-langs")]}.{arguments[IndexAfter(arguments, "--convert-subs")]}";
                    File.WriteAllText(Path.Combine(home, name), SubtitleText);
                }

                return 0;
            }

            File.WriteAllBytes(Path.Combine(temp, stem + ".f137.mp4.part"), new byte[1000]);
            onOutput("[ob2] 137|downloading|1000|NA|4000.0|500.0");
            Started.TrySetResult();
            if (HangAfterProgress)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken); // stopped the way the real runner is
            }

            onOutput("[ob2] 137|finished|4000|4000|NA|NA");
            onOutput("[ob2] 140|finished|1000|1000|NA|NA");
            var file = Path.Combine(home, stem + ".mp4");
            File.WriteAllBytes(file, new byte[FileSize]);
            onOutput("[ob2-file] " + (GarblePrintedPath ? Path.Combine(home, new string(' ', stem.Length) + ".mp4") : file));
            return 0;
        }

        private static string Value(IReadOnlyList<string> args, string prefix) =>
            args.First(a => a.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

        private static int IndexAfter(IReadOnlyList<string> args, string name) => args.ToList().IndexOf(name) + 1;
    }
}
