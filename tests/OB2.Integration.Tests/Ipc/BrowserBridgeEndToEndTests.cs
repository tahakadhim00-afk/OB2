using System.Diagnostics;
using System.Text.Json;
using OB2.Integration.Tests.Infrastructure;
using OB2.Shared;
using OB2.Shared.Protocol;
using OB2.Tests.Common;

namespace OB2.Integration.Tests.Ipc;

/// <summary>
/// The real host.exe and OB2.exe, as Chrome would drive them: framed messages on the host's
/// stdin, replies on its stdout. A portable copy in a temp folder keeps this run away from the
/// owner's settings and pipe. The app runs tray-only (<c>--from-host</c>).
/// Covers the app side of AC-5 and AC-6, and AC-13; Chrome itself is the manual part.
/// </summary>
[Collection("OB2.exe")]
public sealed class BrowserBridgeEndToEndTests : IAsyncLifetime
{
    private const string SecretCookie = "ob2session=TOPSECRET-c00k1e-value";
    private const string Origin = "chrome-extension://" + ProtocolInfo.ExtensionId + "/";

    private readonly TempDirectory _temp = new();
    private readonly string _pipeName = "OB2-e2e-" + Guid.NewGuid().ToString("N")[..12];
    private TestHttpServer _server = null!;

    private string AppFolder => _temp.Combine("OB2");

    private string Out => _temp.Combine("downloads");

    public async Task InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        CopyBuild(Path.GetDirectoryName(Ob2Process.ExePath)!, AppFolder);
        CopyBuild(HostBuildFolder(), AppFolder);
        File.WriteAllText(Path.Combine(AppFolder, "portable.flag"), string.Empty);
        var settings = new
        {
            folders = new { defaultSaveFolder = Out },
            ipc = new { pipeName = _pipeName },
            capture = new { confirmBeforeAdding = false },
            general = new { showNotifications = false, showCompleteDialog = false },
            logging = new { minimumLevel = "Debug" },
        };
        File.WriteAllText(Path.Combine(AppFolder, "settings.json"), JsonSerializer.Serialize(settings));
    }

    public async Task DisposeAsync()
    {
        foreach (var process in AppProcesses())
        {
            process.Kill();
            await process.WaitForExitAsync();
            process.Dispose();
        }

        await _server.DisposeAsync();
        _temp.Dispose();
    }

    [Fact]
    public async Task Captured_link_starts_the_app_downloads_with_cookies_and_never_logs_them()
    {
        const long size = 3_000_000;
        var url = _server.FileUrl(size, "capture.bin", "cookie=" + Uri.EscapeDataString(SecretCookie)).AbsoluteUri;
        AppProcesses().Should().BeEmpty();

        // AC-6: the app is closed; the host starts it and the item is queued within 5 seconds.
        await using (var host = HostProcess.Start(Path.Combine(AppFolder, "host.exe"), Origin))
        {
            var stopwatch = Stopwatch.StartNew();
            var ack = await host.SendAsync(Capture(url));
            stopwatch.Stop();

            ack.Type.Should().Be(MessageTypes.Ack, ack.Payload?.ToString());
            ack.PayloadAs<AckPayload>()!.DownloadId.Should().NotBeNull();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "AC-6");
        }

        AppProcesses().Should().ContainSingle("the host started OB2 from its own folder");
        var file = await WaitForFileAsync("capture.bin", size);
        EngineFixture.Sha256OfFile(file).Should().Be(TestHttpServer.Sha256Of(size), "the server only serves this file with the browser's cookie");
        var fileRequest = _server.Requests.Last(r => r.PathAndQuery.Contains("capture.bin", StringComparison.Ordinal));
        fileRequest.Header("Referer").Should().Be("https://example.com/downloads");

        // AC-5 (app side): with the app running, a second capture is acknowledged within 2 seconds.
        await using (var host = HostProcess.Start(Path.Combine(AppFolder, "host.exe"), Origin))
        {
            var pong = await host.SendAsync(ProtocolMessage.Simple(MessageTypes.Ping));
            pong.PayloadAs<PongPayload>()!.AppRunning.Should().BeTrue();

            var stopwatch = Stopwatch.StartNew();
            var ack = await host.SendAsync(Capture(_server.FileUrl(1_000_000, "second.bin", "cookie=" + Uri.EscapeDataString(SecretCookie)).AbsoluteUri));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "AC-5");
            ack.PayloadAs<AckPayload>()!.DownloadId.Should().NotBeNull();

            await WaitForFileAsync("second.bin", 1_000_000);
            var status = (await host.SendAsync(ProtocolMessage.Simple(MessageTypes.GetStatus))).PayloadAs<StatusPayload>()!;
            status.Recent.Select(d => d.FileName).Should().Contain(["capture.bin", "second.bin"]);
            status.Recent.Should().OnlyContain(d => d.Status == "completed");
            status.CaptureRules.MinSizeBytes.Should().Be(1024 * 1024);
            status.CaptureRules.Extensions.Should().Contain("zip");

            (await host.SendAsync(ProtocolMessage.Simple(MessageTypes.PauseAll))).PayloadAs<AckPayload>()!.Status.Should().Be("paused");
            (await host.SendAsync(ProtocolMessage.Simple(MessageTypes.ResumeAll))).PayloadAs<AckPayload>()!.Status.Should().Be("resumed");
        }

        // AC-13: no cookie value in any log file.
        var logs = Directory.GetFiles(Path.Combine(AppFolder, "data", "logs"), "*.log");
        logs.Should().NotBeEmpty();
        var text = string.Concat(logs.Select(ReadShared));
        text.Should().Contain("(cookies: true)", "the capture was logged, without its value");
        text.Should().NotContain("TOPSECRET");
        text.Should().NotContain("c00k1e");
    }

    [Fact]
    public async Task The_video_buttons_quality_menu_starts_the_app_and_a_picked_quality_is_added_directly()
    {
        await using var host = HostProcess.Start(Path.Combine(AppFolder, "host.exe"), Origin);

        // D-72: a click on the button starts OB2 like a capture. This copy has no yt-dlp yet.
        var formats = await host.SendAsync(ProtocolMessage.Create(MessageTypes.GetMediaFormats, new MediaFormatsRequest { Url = "https://youtu.be/dQw4w9WgXcQ" }));
        formats.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.MediaToolsMissing);
        AppProcesses().Should().ContainSingle();

        // With confirmation off, a quality picked on the button needs no window.
        var ack = await host.SendAsync(ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload
        {
            Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42",
            MediaPage = true,
            MediaFormat = "137+140",
            MediaContainer = "mp4",
            FilenameHint = "Launch day.mp4",
            FileSizeHint = 108_400_000,
            Source = "chrome-media",
        }));
        ack.Type.Should().Be(MessageTypes.Ack, ack.Payload?.ToString());
        ack.PayloadAs<AckPayload>()!.DownloadId.Should().NotBeNull();

        // D-74: subtitles picked on the button are a download of their own, named with their language.
        var subtitles = await host.SendAsync(ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload
        {
            Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            MediaPage = true,
            MediaFormat = "auto-subs:en-orig",
            MediaContainer = "srt",
            FilenameHint = "Launch day.srt",
            Source = "chrome-media",
        }));
        subtitles.Type.Should().Be(MessageTypes.Ack, subtitles.Payload?.ToString());

        var status = (await host.SendAsync(ProtocolMessage.Simple(MessageTypes.GetStatus))).PayloadAs<StatusPayload>()!;
        status.Active.Concat(status.Recent).Select(d => d.FileName).Should().Contain(["Launch day.mp4", "Launch day.en-orig.srt"]);
    }

    [Fact]
    public async Task Without_the_app_the_popups_requests_do_not_start_it()
    {
        await using var host = HostProcess.Start(Path.Combine(AppFolder, "host.exe"), Origin);

        var pong = await host.SendAsync(ProtocolMessage.Simple(MessageTypes.Ping));
        var status = await host.SendAsync(ProtocolMessage.Simple(MessageTypes.GetStatus));

        pong.PayloadAs<PongPayload>()!.AppRunning.Should().BeFalse();
        status.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.AppNotRunning);
        AppProcesses().Should().BeEmpty("a popup opening must not launch OB2");
    }

    [Fact]
    public async Task Host_rejects_bad_urls_without_starting_the_app()
    {
        await using var host = HostProcess.Start(Path.Combine(AppFolder, "host.exe"), Origin);

        var reply = await host.SendAsync(Capture("file:///C:/Windows/win.ini"));

        reply.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.UnsupportedScheme);
        AppProcesses().Should().BeEmpty();
    }

    [Fact]
    public async Task Host_answers_a_malformed_frame_and_exits()
    {
        await using var host = HostProcess.Start(Path.Combine(AppFolder, "host.exe"), Origin);

        var reply = await host.SendRawAsync([3, 0, 0, 0, (byte)'{', (byte)'x', (byte)'}']);

        reply.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.InvalidMessage);
        (await host.WaitForExitAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Host_refuses_other_extensions()
    {
        await using var host = HostProcess.Start(Path.Combine(AppFolder, "host.exe"), "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/");

        (await host.WaitForExitAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Host_exits_cleanly_when_Chrome_closes_the_port()
    {
        await using var host = HostProcess.Start(Path.Combine(AppFolder, "host.exe"), Origin);
        host.CloseInput();

        (await host.WaitForExitAsync()).Should().Be(0);
    }

    private static ProtocolMessage Capture(string url) => ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload
    {
        Url = url,
        Referrer = "https://example.com/downloads",
        FilenameHint = Path.GetFileName(new Uri(url).AbsolutePath),
        UserAgent = "Mozilla/5.0 (OB2 test)",
        Cookies = SecretCookie,
        Source = "chrome-downloads",
    });

    private async Task<string> WaitForFileAsync(string name, long size)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(Out) && Directory.GetFiles(Out, name, SearchOption.AllDirectories).FirstOrDefault() is { } path && new FileInfo(path).Length == size)
            {
                return path;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"{name} was not downloaded. Log: {string.Concat(Directory.GetFiles(Path.Combine(AppFolder, "data", "logs")).Select(ReadShared))}");
    }

    private Process[] AppProcesses() => Process.GetProcessesByName("OB2").Where(p =>
    {
        try
        {
            return p.MainModule?.FileName.StartsWith(AppFolder, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }).ToArray();

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void CopyBuild(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string HostBuildFolder()
    {
        var appBin = new DirectoryInfo(Path.GetDirectoryName(Ob2Process.ExePath)!); // src/OB2.App/bin/<config>/net8.0-windows
        var src = appBin.Parent!.Parent!.Parent!.Parent!.FullName;
        var folder = Path.Combine(src, "OB2.Host", "bin", appBin.Parent.Name, "net10.0");
        return File.Exists(Path.Combine(folder, "host.exe")) ? folder : throw new FileNotFoundException("Build OB2.Host first.", folder);
    }

    /// <summary>host.exe with Chrome's side of the stdio framing.</summary>
    private sealed class HostProcess : IAsyncDisposable
    {
        private readonly Process _process;

        private HostProcess(Process process) => _process = process;

        public static HostProcess Start(string exe, string origin)
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add(origin);
            return new HostProcess(Process.Start(info)!);
        }

        public async Task<ProtocolMessage> SendAsync(ProtocolMessage request)
        {
            await MessageFraming.WriteAsync(_process.StandardInput.BaseStream, request, CancellationToken.None);
            return await ReadAsync();
        }

        public async Task<ProtocolMessage> SendRawAsync(byte[] bytes)
        {
            await _process.StandardInput.BaseStream.WriteAsync(bytes);
            await _process.StandardInput.BaseStream.FlushAsync();
            return await ReadAsync();
        }

        public void CloseInput() => _process.StandardInput.Close();

        public async Task<int> WaitForExitAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(cts.Token);
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                await _process.WaitForExitAsync();
            }

            _process.Dispose();
        }

        private async Task<ProtocolMessage> ReadAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await MessageFraming.ReadAsync(_process.StandardOutput.BaseStream, cts.Token)
                ?? throw new EndOfStreamException("host.exe closed stdout without replying.");
        }
    }
}
