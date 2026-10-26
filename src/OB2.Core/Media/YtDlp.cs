using System.Globalization;
using System.Text;
using OB2.Core.Downloads;
using OB2.Core.Http;
using OB2.Core.Time;

namespace OB2.Core.Media;

/// <summary>One progress line from yt-dlp, for one format (video and audio download one after the other).</summary>
public sealed record YtDlpProgress(string FormatId, string Status, long? Downloaded, long? Total, double? BytesPerSecond);

/// <summary>The yt-dlp command lines OB2 runs and how it reads their output (D-71).</summary>
public static class YtDlp
{
    public const string ProgressPrefix = "[ob2] ";
    public const string FilePrefix = "[ob2-file] ";

    /// <summary>Machine-readable progress: format id, status, bytes so far, total, estimated total, speed.</summary>
    public const string ProgressTemplate =
        ProgressPrefix + "%(info.format_id)s|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s";

    /// <summary>
    /// <c>yt-dlp -J</c>: everything about one video, formats included, as JSON on one line.
    /// D-79: <paramref name="referrer"/> is the page whose player loaded <paramref name="url"/>.
    /// D-80: <paramref name="headers"/> and <paramref name="userAgent"/> are what the browser sent for it.
    /// </summary>
    public static IReadOnlyList<string> InfoArguments(MediaTools tools, Uri url, Uri? referrer = null, IReadOnlyDictionary<string, string>? headers = null, string? userAgent = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(url);
        return [.. tools.CommonArguments, .. RefererArguments(url.AbsoluteUri, referrer?.AbsoluteUri), .. BrowserArguments(url.AbsoluteUri, headers, userAgent), "--dump-single-json", "--no-playlist", "--", url.AbsoluteUri];
    }

    /// <summary>
    /// Downloads the chosen formats. Parts go to <see cref="DownloadItem.MediaPartsFolder"/> and
    /// are kept when the process is stopped, so the next run continues them; the finished file
    /// lands at <see cref="DownloadItem.DestPath"/> and its path is printed after
    /// <see cref="FilePrefix"/>.
    /// </summary>
    public static IReadOnlyList<string> DownloadArguments(MediaTools tools, DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(item);
        var format = item.MediaFormat ?? throw new ArgumentException("Not a media download.", nameof(item));
        if (SubtitleTrack.TryParseSpec(format, out var language, out var automatic))
        {
            return SubtitleArguments(tools, item, language, automatic);
        }

        var args = new List<string>([.. tools.CommonArguments, .. RefererArguments(item.Url, item.Referrer), .. BrowserArguments(item.Url, item.Headers, item.UserAgent)])
        {
            "--no-playlist",
            "--newline",
            "--progress",
            "--progress-template", "download:" + ProgressTemplate,
            // --print implies --quiet; --progress above keeps the progress lines.
            "--print", "after_move:" + FilePrefix + "%(filepath)s",
            "-f", format,
            "-P", "home:" + item.SaveFolder,
            "-P", "temp:" + item.MediaPartsFolder,
            "-o", EscapeTemplate(Path.GetFileNameWithoutExtension(item.DestPath)) + ".%(ext)s",
            "--no-overwrites",
            "--continue",
            "--no-mtime",
            // Streams in pieces (HLS/DASH sites) load several pieces at once, like OB2's own
            // segments; servers often limit each connection.
            "--concurrent-fragments", "4",
        };

        if (format.Contains('+', StringComparison.Ordinal) && item.MediaContainer is { Length: > 0 } container)
        {
            args.Add("--merge-output-format");
            args.Add(container);
        }

        if (item.SpeedLimitBps is { } rate && rate > 0)
        {
            // FR-9 per download. The global limit can't reach inside yt-dlp.
            args.Add("--limit-rate");
            args.Add(rate.ToString(CultureInfo.InvariantCulture));
        }

        args.Add("--");
        args.Add(item.Url);
        return args;
    }

    /// <summary>
    /// D-74: one subtitle track, saved as <see cref="DownloadItem.MediaContainer"/>. With
    /// <c>--skip-download</c> yt-dlp prints no final path, so it writes into the parts folder under
    /// its own name and <c>MediaJob</c> moves the result to <see cref="DownloadItem.DestPath"/>.
    /// </summary>
    private static List<string> SubtitleArguments(MediaTools tools, DownloadItem item, string language, bool automatic)
    {
        var format = item.MediaContainer is { } c && SubtitleTrack.Formats.Contains(c)
            ? c
            : throw new ArgumentException("Subtitles need srt, ass or vtt as their container.", nameof(item));

        // The site's own SRT or ASS is taken as it is; otherwise SRT is converted before VTT.
        // YouTube's auto-generated VTT repeats every line as it scrolls and carries karaoke tags
        // into the converted file; its SRT has each line once.
        string[] preference = format == "vtt" ? ["srt", "vtt", "ass", "ssa"] : [format, "srt", "vtt", "ass", "ssa"];
        var args = new List<string>([.. tools.CommonArguments, .. RefererArguments(item.Url, item.Referrer), .. BrowserArguments(item.Url, item.Headers, item.UserAgent)])
        {
            "--no-playlist",
            "--newline",
            "--progress",
            "--progress-template", "download:" + ProgressTemplate,
            "--skip-download",
            automatic ? "--write-auto-subs" : "--write-subs",
            "--sub-langs", language,
            "--sub-format", string.Join('/', preference.Distinct()),
            "--convert-subs", format,
            "-P", "home:" + item.MediaPartsFolder,
            "-P", "temp:" + item.MediaPartsFolder,
            "-o", "subtitle:subtitles.%(ext)s",
        };

        if (item.SpeedLimitBps is { } rate && rate > 0)
        {
            args.Add("--limit-rate");
            args.Add(rate.ToString(CultureInfo.InvariantCulture));
        }

        args.Add("--");
        args.Add(item.Url);
        return args;
    }

    /// <summary>D-74: the subtitle file yt-dlp left in the parts folder, or null when it saved none.</summary>
    public static string? SubtitleFile(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Directory.Exists(item.MediaPartsFolder)
            ? Directory.EnumerateFiles(item.MediaPartsFolder).FirstOrDefault(f => string.Equals(Path.GetExtension(f), "." + item.MediaContainer, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    public static YtDlpProgress? ParseProgress(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = line[ProgressPrefix.Length..].Split('|');
        if (parts.Length < 6)
        {
            return null;
        }

        var total = Bytes(parts[3]) ?? Bytes(parts[4]);
        return new YtDlpProgress(parts[0], parts[1], Bytes(parts[2]), total, Number(parts[5]));
    }

    public static string? ParseFile(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.StartsWith(FilePrefix, StringComparison.Ordinal) && line.Length > FilePrefix.Length ? line[FilePrefix.Length..].Trim() : null;
    }

    /// <summary>
    /// The reason from yt-dlp's last <c>ERROR:</c> line, without the prefix and the
    /// <c>[site] id:</c> part: "Sign in to confirm you're not a bot…".
    /// </summary>
    public static string? ErrorMessage(IEnumerable<string> stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        var line = stderr.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var message = line["ERROR:".Length..].Trim();
        if (message.StartsWith('['))
        {
            var close = message.IndexOf(']', StringComparison.Ordinal);
            var colon = close > 0 ? message.IndexOf(": ", close, StringComparison.Ordinal) : -1;
            message = colon > 0 ? message[(colon + 2)..] : message[(close + 1)..].Trim();
        }

        return message.Length > 400 ? message[..400] + "…" : message;
    }

    /// <summary>D-79: <see cref="ErrorMessage"/> says yt-dlp has no extractor for the site and found nothing in the page.</summary>
    public static bool IsUnsupported(string? message) => message?.StartsWith("Unsupported URL", StringComparison.Ordinal) == true;

    /// <summary>
    /// D-79: a stream that a player loaded is often refused without the page that embeds it, as
    /// the browser sent it. YouTube needs none and gets none, so its downloads run as before.
    /// </summary>
    private static string[] RefererArguments(string url, string? referrer) =>
        Uri.TryCreate(referrer, UriKind.Absolute, out var page) && page.Scheme is "http" or "https"
        && !string.Equals(page.AbsoluteUri, url, StringComparison.Ordinal)
        && !(Uri.TryCreate(url, UriKind.Absolute, out var target) && VideoSites.YouTubeVideo(target) is not null)
            ? ["--referer", page.AbsoluteUri]
            : [];

    /// <summary>
    /// D-80: the headers the browser sent for a stream (Origin, the player's own tokens) and its
    /// user agent, only for a stream that came with them: yt-dlp's extractors set their own per
    /// site, and YouTube's would break with another user agent. Cookies and Authorization never
    /// go on a command line (D-71); OB2's own stream job sends cookies itself.
    /// </summary>
    private static string[] BrowserArguments(string url, IReadOnlyDictionary<string, string>? headers, string? userAgent)
    {
        var kept = DownloadHttpClient.PersistableHeaders(headers);
        if (kept.Count == 0 || (Uri.TryCreate(url, UriKind.Absolute, out var target) && VideoSites.YouTubeVideo(target) is not null))
        {
            return [];
        }

        var args = new List<string>();
        if (userAgent is { Length: > 0 })
        {
            args.AddRange(["--user-agent", userAgent]);
        }

        foreach (var (name, value) in kept)
        {
            args.AddRange(["--add-header", $"{name}:{value}"]);
        }

        return [.. args];
    }

    /// <summary>In an output template only <c>%</c> is special.</summary>
    private static string EscapeTemplate(string text) => text.Replace("%", "%%", StringComparison.Ordinal);

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : null;

    private static long? Bytes(string text) => Number(text) is { } value ? (long)value : null;
}

/// <summary>
/// Runs <c>yt-dlp -J</c> to list what a video page offers. Answers are kept for a few minutes
/// (D-72): the browser's button lists the qualities, then the download or the Download Video
/// window needs the same list, and yt-dlp takes seconds to produce it.
/// </summary>
public sealed class MediaInspector
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);
    private const int KeepAtMost = 20;

    private readonly MediaTools _tools;
    private readonly IClock _clock;
    private readonly StreamManifestReader? _streams;
    private readonly Dictionary<string, (MediaInfo Info, DateTimeOffset At)> _recent = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <param name="streams">D-80: OB2's own reader for HLS and DASH streams; without it streams go to yt-dlp alone.</param>
    public MediaInspector(MediaTools tools, IClock? clock = null, StreamManifestReader? streams = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _clock = clock ?? SystemClock.Instance;
        _streams = streams;
    }

    public MediaTools Tools => _tools;

    /// <summary>D-79: the most streams tried for one page; each is a yt-dlp run of a few seconds.</summary>
    public const int MaxStreams = 5;

    /// <summary>
    /// D-80: a stream as the page's player requested it. OB2 reads it itself first (its formats
    /// then download without yt-dlp); when that fails, yt-dlp tries with the same headers. A stream
    /// that is copy-protected or live is refused at once.
    /// </summary>
    /// <exception cref="MediaException">Neither could read it; the reason is OB2's own, the clearer one.</exception>
    public async Task<MediaInfo> InspectStreamAsync(StreamRequest stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!_tools.IsInstalled)
        {
            throw new MediaException("yt-dlp, ffmpeg and Deno are not installed.");
        }

        lock (_lock)
        {
            if (_recent.TryGetValue(stream.Url.AbsoluteUri, out var kept) && _clock.UtcNow - kept.At < KeepFor)
            {
                return kept.Info;
            }
        }

        MediaException? own = null;
        if (_streams is not null)
        {
            try
            {
                var info = (await _streams.ReadAsync(stream, cancellationToken).ConfigureAwait(false)).ToMediaInfo();
                Keep(stream.Url, info);
                return info;
            }
            catch (MediaException ex) when (ex.Conclusive)
            {
                throw;
            }
            catch (MediaException ex)
            {
                own = ex;
            }
            catch (HttpStatusException ex)
            {
                own = new MediaException($"The stream's server refused it: {ex.Message}", ex);
            }
            catch (HttpRequestException ex)
            {
                own = new MediaException($"The stream's server could not be reached: {ex.Message}", ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                own = new MediaException("The stream's server did not answer in time.", ex);
            }
        }

        try
        {
            var referrer = Uri.TryCreate(stream.Referrer, UriKind.Absolute, out var page) ? page : null;
            var info = await RunAsync(stream.Url, referrer, stream.Headers, stream.UserAgent, cancellationToken).ConfigureAwait(false);
            Keep(stream.Url, info);
            return info;
        }
        catch (MediaException) when (own is not null)
        {
            throw own;
        }
    }

    /// <exception cref="MediaException">yt-dlp failed (unsupported site, private video, network…).</exception>
    public Task<MediaInfo> InspectAsync(Uri url, CancellationToken cancellationToken = default) => InspectAsync(url, null, cancellationToken);

    /// <param name="referrer">D-79: the page whose player loaded <paramref name="url"/>, sent as Referer.</param>
    /// <exception cref="MediaException">yt-dlp failed (unsupported site, private video, network…).</exception>
    public async Task<MediaInfo> InspectAsync(Uri url, Uri? referrer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!_tools.IsInstalled)
        {
            throw new MediaException("yt-dlp, ffmpeg and Deno are not installed.");
        }

        lock (_lock)
        {
            if (_recent.TryGetValue(url.AbsoluteUri, out var kept) && _clock.UtcNow - kept.At < KeepFor)
            {
                return kept.Info;
            }
        }

        var info = await RunAsync(url, referrer, null, null, cancellationToken).ConfigureAwait(false);
        Keep(url, info);
        return info;
    }

    /// <summary>
    /// D-79: what a video page offers, from the page or from a stream its player loaded. yt-dlp
    /// reads the page first: a site it knows gives the title, every audio track and the
    /// subtitles. When it doesn't know the site (it failed, or only guessed from the page's
    /// HTML), the <paramref name="streams"/> are tried in the order the player loaded them
    /// (D-80: as the browser requested them, <see cref="InspectStreamAsync"/>), and the first one
    /// that has formats wins. Its answer carries <paramref name="title"/> (the tab's), since a
    /// stream's own name is "master" or "index", and is kept under the stream's address for the
    /// download that follows.
    /// </summary>
    /// <returns>The address the formats belong to: <paramref name="page"/> or one of <paramref name="streams"/>.</returns>
    /// <exception cref="MediaException">Neither the page nor any stream could be read.</exception>
    public async Task<(Uri Source, MediaInfo Info)> InspectPageAsync(Uri page, IReadOnlyList<StreamRequest> streams, string? title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(streams);
        MediaInfo? fromPage = null;
        MediaException? pageFailure = null;
        try
        {
            fromPage = await InspectAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaException ex) when (streams.Count > 0)
        {
            pageFailure = ex;
        }
        catch (MediaException ex) when (ex.Unsupported)
        {
            throw new MediaException("yt-dlp does not support this site, and the player has not loaded a stream OB2 can read yet. Play the video for a moment, then try again.", ex) { Unsupported = true };
        }

        if (fromPage is { Formats.Count: > 0 } && (streams.Count == 0 || !fromPage.IsGeneric))
        {
            return (page, fromPage);
        }

        MediaException? streamFailure = null;
        foreach (var stream in streams.Take(MaxStreams))
        {
            try
            {
                var info = await InspectStreamAsync(stream, cancellationToken).ConfigureAwait(false);
                if (info.Formats.Count > 0)
                {
                    if (title is { Length: > 0 } && (info.IsGeneric || info.FromStream))
                    {
                        info = info with { Title = title };
                        Keep(stream.Url, info);
                    }

                    return (stream.Url, info);
                }
            }
            catch (MediaException ex) when (ex.Conclusive)
            {
                throw; // copy-protected or live: another stream of the same player is no different
            }
            catch (MediaException ex)
            {
                streamFailure ??= ex;
            }
        }

        if (fromPage is not null)
        {
            return (page, fromPage); // a guess from the page's HTML, or nothing to offer
        }

        throw pageFailure is { Unsupported: true } && streamFailure is not null
            ? new MediaException($"yt-dlp does not support this site, and OB2 could not read the video's stream either: {streamFailure.Message}", streamFailure) { Unsupported = true }
            : pageFailure!;
    }

    private void Keep(Uri url, MediaInfo info)
    {
        lock (_lock)
        {
            _recent[url.AbsoluteUri] = (info, _clock.UtcNow);
            if (_recent.Count > KeepAtMost)
            {
                _recent.Remove(_recent.MinBy(e => e.Value.At).Key);
            }
        }
    }

    private async Task<MediaInfo> RunAsync(Uri url, Uri? referrer, IReadOnlyDictionary<string, string>? headers, string? userAgent, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var errors = new List<string>();
        int code;
        try
        {
            code = await _tools.Runner.RunAsync(_tools.YtDlpPath, YtDlp.InfoArguments(_tools, url, referrer, headers, userAgent), line => output.AppendLine(line), errors.Add, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new MediaException($"yt-dlp could not be started: {ex.Message}", ex);
        }

        if (code != 0)
        {
            var reason = YtDlp.ErrorMessage(errors);
            throw YtDlp.IsUnsupported(reason)
                ? new MediaException("yt-dlp does not support this site.") { Unsupported = true }
                : new MediaException(reason ?? $"yt-dlp stopped with exit code {code}.");
        }

        return MediaInfo.Parse(output.ToString());
    }
}
