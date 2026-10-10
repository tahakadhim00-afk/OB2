using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using OB2.Core.Downloads;
using OB2.Core.Files;
using OB2.Core.Http;
using OB2.Core.Media;
using OB2.Core.Services;
using OB2.Core.Settings;
using OB2.Shared;
using OB2.Shared.Ipc;
using OB2.Shared.Protocol;

namespace OB2.App.Services;

/// <summary>
/// Handles requests arriving over the named pipe from host.exe or a second ob2.exe (§8.3).
/// Replies within the 2-second budget: a captured download is either added straight away
/// from the browser's hints (the job probes the server when it starts) or queued for the
/// confirmation dialog (FR-22). Cookie values are never logged.
/// </summary>
public sealed partial class BrowserBridge : IIpcHandler
{
    private readonly DownloadService _service;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<BrowserBridge> _logger;

    public BrowserBridge(DownloadService service, AppSettings settings, IDialogService dialogs, ILogger<BrowserBridge> logger)
    {
        _service = service;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Set by the app: show and focus the main window.</summary>
    public Action? ActivateWindow { get; set; }

    public async Task<ProtocolMessage> HandleAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Type)
        {
            case MessageTypes.AddDownload:
                return await AddAsync(request, cancellationToken);
            case MessageTypes.GetStatus:
                return ProtocolMessage.Create(MessageTypes.Status, BuildStatus(), request.Id);
            case MessageTypes.PauseAll:
                await _service.StopAllAsync();
                return ProtocolMessage.Create(MessageTypes.Ack, new AckPayload(null, "paused"), request.Id);
            case MessageTypes.ResumeAll:
                _service.StartAll();
                return ProtocolMessage.Create(MessageTypes.Ack, new AckPayload(null, "resumed"), request.Id);
            case MessageTypes.Ping:
                return ProtocolMessage.Create(MessageTypes.Pong, new PongPayload(true, ProtocolInfo.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)), request.Id);
            case MessageTypes.Activate:
                _ = _dispatcher.BeginInvoke(() => ActivateWindow?.Invoke());
                return ProtocolMessage.Create(MessageTypes.Ack, new AckPayload(null, "activated"), request.Id);
            default:
                return ProtocolMessage.ErrorReply(request.Id, ProtocolErrors.InvalidMessage, $"Unknown message type '{request.Type}'.");
        }
    }

    private async Task<ProtocolMessage> AddAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        var payload = request.PayloadAs<AddDownloadPayload>()!;
        var url = payload.FinalUrl is { Length: > 0 } ? payload.FinalUrl : payload.Url!;
        var source = payload.Source switch
        {
            "cli" => DownloadSource.Cli,
            "clipboard" => DownloadSource.Clipboard,
            _ => DownloadSource.Chrome,
        };
        LogCaptured(request.Id, source, Core.Logging.LogRedactor.Url(url), !string.IsNullOrEmpty(payload.Cookies));

        var prefill = new AddDownloadPrefill
        {
            Url = url,
            FileNameHint = payload.FilenameHint,
            Referrer = payload.Referrer,
            Cookies = payload.Cookies,
            UserAgent = payload.UserAgent,
            Headers = payload.Headers,
            FileSizeHint = payload.FileSizeHint,
            Source = source,
            MediaPage = payload.MediaPage == true,
        };

        // §11.1: a URL with embedded credentials (user:pass@host) is only accepted after the user confirms it.
        var hasCredentials = new Uri(url).UserInfo.Length > 0;
        // D-71: a video page always needs the user's choice of quality and audio.
        var mediaPage = prefill.MediaPage || VideoSites.YouTubeVideo(new Uri(url)) is not null;
        var confirm = mediaPage || source == DownloadSource.Cli || hasCredentials || (_settings.Capture.ConfirmBeforeAdding && !payload.AutoStart);
        if (confirm)
        {
            // FR-22: show the Add dialog; reply now so the browser can cancel its own download.
            _ = _dispatcher.BeginInvoke(() =>
            {
                ActivateWindow?.Invoke();
                _dialogs.ShowAddDownload(prefill);
            });
            return ProtocolMessage.Create(MessageTypes.Ack, new AckPayload(null, "confirming"), request.Id);
        }

        var name = FileNameSanitizer.Sanitize(payload.FilenameHint ?? FileNameResolver.FromUrl(new Uri(url)));
        var probe = new ProbeResult(new Uri(url), payload.FileSizeHint is > 0 ? payload.FileSizeHint : null, false, name, null, null, payload.MimeType);
        var item = await _service.AddAsync(
            new AddDownloadOptions
            {
                Url = new Uri(url),
                FileNameHint = payload.FilenameHint,
                Referrer = payload.Referrer,
                Cookies = payload.Cookies,
                UserAgent = payload.UserAgent,
                Headers = payload.Headers ?? new Dictionary<string, string>(),
                Source = source,
                StartNow = true,
            },
            probe,
            cancellationToken);
        return ProtocolMessage.Create(MessageTypes.Ack, new AckPayload(item.Id, item.Status.ToDbString()), request.Id);
    }

    private StatusPayload BuildStatus()
    {
        var all = _service.List();
        DownloadSummary Summary(DownloadItem d)
        {
            var progress = _service.Engine.GetProgress(d.Id);
            var total = progress?.TotalBytes ?? d.TotalBytes;
            var done = progress?.DownloadedBytes ?? d.DownloadedBytes;
            return new DownloadSummary(d.Id, d.FileName, (progress?.Status ?? d.Status).ToDbString(),
                total is > 0 ? Math.Round(done * 100.0 / total.Value, 1) : null,
                Math.Round(progress?.BytesPerSecond ?? 0), total);
        }

        var active = all.Where(d => _service.Engine.IsActive(d.Id)).Select(Summary).ToList();
        var recent = all.Where(d => !_service.Engine.IsActive(d.Id)).OrderByDescending(d => d.CompletedAt ?? d.CreatedAt).Take(8).Select(Summary).ToList();
        var c = _settings.Capture;
        return new StatusPayload(active, recent, new CaptureRulesPayload(true, c.MinSizeBytes, c.Extensions, c.ExcludedDomains));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Request {Id}: {Source} capture of {Url} (cookies: {HasCookies})")]
    private partial void LogCaptured(string id, DownloadSource source, string url, bool hasCookies);
}
