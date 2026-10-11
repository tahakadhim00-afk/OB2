using System.Globalization;
using System.Text;
using OB2.Core.Downloads;
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

    /// <summary><c>yt-dlp -J</c>: everything about one video, formats included, as JSON on one line.</summary>
    public static IReadOnlyList<string> InfoArguments(MediaTools tools, Uri url)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(url);
        return [.. tools.CommonArguments, "--dump-single-json", "--no-playlist", "--", url.AbsoluteUri];
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

        var args = new List<string>(tools.CommonArguments)
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
    private readonly Dictionary<string, (MediaInfo Info, DateTimeOffset At)> _recent = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public MediaInspector(MediaTools tools, IClock? clock = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _clock = clock ?? SystemClock.Instance;
    }

    public MediaTools Tools => _tools;

    /// <exception cref="MediaException">yt-dlp failed (unsupported site, private video, network…).</exception>
    public async Task<MediaInfo> InspectAsync(Uri url, CancellationToken cancellationToken = default)
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

        var info = await RunAsync(url, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _recent[url.AbsoluteUri] = (info, _clock.UtcNow);
            if (_recent.Count > KeepAtMost)
            {
                _recent.Remove(_recent.MinBy(e => e.Value.At).Key);
            }
        }

        return info;
    }

    private async Task<MediaInfo> RunAsync(Uri url, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var errors = new List<string>();
        int code;
        try
        {
            code = await _tools.Runner.RunAsync(_tools.YtDlpPath, YtDlp.InfoArguments(_tools, url), line => output.AppendLine(line), errors.Add, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new MediaException($"yt-dlp could not be started: {ex.Message}", ex);
        }

        if (code != 0)
        {
            throw new MediaException(YtDlp.ErrorMessage(errors) ?? $"yt-dlp stopped with exit code {code}.");
        }

        return MediaInfo.Parse(output.ToString());
    }
}
