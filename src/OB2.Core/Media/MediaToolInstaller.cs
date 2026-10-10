using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OB2.Core.Downloads;
using OB2.Core.Engine;

namespace OB2.Core.Media;

/// <summary>Progress of one tool's download, for the Download Video window.</summary>
public sealed record ToolDownloadProgress(string Tool, long Received, long? Total, double BytesPerSecond);

/// <summary>
/// Downloads yt-dlp, Deno and ffmpeg from their official GitHub releases into
/// <see cref="MediaTools.Folder"/> the first time a video page is opened (D-71, the owner's
/// choice), and updates yt-dlp on request. The files come through OB2's own engine (several
/// connections, retries, expired-link refresh), so about 150 MB also arrive on a slow line, and a
/// stopped download continues where it was, even after a restart. Every file is checked against
/// the SHA-256 its release publishes before it replaces anything.
/// </summary>
public sealed partial class MediaToolInstaller : IDisposable
{
    public const string YtDlpRelease = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    public const string DenoRelease = "https://github.com/denoland/deno/releases/latest/download/";
    public const string FfmpegRelease = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";

    public const string YtDlpAsset = "yt-dlp.exe";
    public const string DenoAsset = "deno-x86_64-pc-windows-msvc.zip";
    public const string FfmpegAsset = "ffmpeg-master-latest-win64-gpl-shared.zip";

    /// <summary>yt-dlp 18 MB + Deno 43 MB + ffmpeg 89 MB, measured 2026-10-10.</summary>
    public const long ApproximateTotalBytes = 150L * 1024 * 1024;

    /// <summary>
    /// GitHub's CDN limits each connection (measured: 3–20 KB/s on one, about 4× that on four
    /// on a slow line), so the tools use the engine's segmented download.
    /// </summary>
    private static readonly EngineOptions DownloadOptions = new()
    {
        Connections = 8,
        MaxRetries = 10,
        ReadIdleTimeout = TimeSpan.FromSeconds(90),
        UserAgent = "OB2-download-manager",
    };

    private readonly MediaTools _tools;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly HttpClient _http;
    private readonly object _engineLock = new();
    private DownloadEngine? _engine;
    private ToolDownloadStore? _store;

    public MediaToolInstaller(MediaTools tools, ILoggerFactory? loggerFactory = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _loggerFactory = loggerFactory;
        // Only for the small checksum files; the tools themselves go through the engine.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(DownloadOptions.UserAgent);
    }

    public MediaTools Tools => _tools;

    /// <summary>Partial tool downloads and their progress record.</summary>
    public string DownloadsFolder => Path.Combine(_tools.Folder, "downloads");

    /// <summary>Installs whichever of the three tools is missing.</summary>
    /// <exception cref="OperationCanceledException">Stopped; the partial files are kept for next time.</exception>
    public async Task InstallAsync(IProgress<ToolDownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_tools.Folder);
        if (!File.Exists(_tools.YtDlpPath))
        {
            await InstallYtDlpAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(_tools.DenoPath))
        {
            var zip = await FetchVerifiedAsync("Deno", DenoRelease + DenoAsset, DenoRelease + DenoAsset + ".sha256sum", DenoAsset, progress, cancellationToken).ConfigureAwait(false);
            ExtractFiles(zip.Path, entry => string.Equals(entry.Name, "deno.exe", StringComparison.OrdinalIgnoreCase), _tools.Folder);
            await ForgetAsync(zip.Id).ConfigureAwait(false);
        }

        if (!File.Exists(_tools.FfmpegPath))
        {
            var zip = await FetchVerifiedAsync("ffmpeg", FfmpegRelease + FfmpegAsset, FfmpegRelease + "checksums.sha256", FfmpegAsset, progress, cancellationToken).ConfigureAwait(false);
            var staging = _tools.FfmpegFolder + ".download";
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }

                // The programs and their DLLs are in "<build>/bin/"; nothing else is needed.
                ExtractFiles(zip.Path, entry => entry.FullName.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase) && entry.Name.Length > 0, staging);
                if (Directory.Exists(_tools.FfmpegFolder))
                {
                    Directory.Delete(_tools.FfmpegFolder, recursive: true);
                }

                Directory.Move(staging, _tools.FfmpegFolder);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }

            await ForgetAsync(zip.Id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replaces yt-dlp with the latest release. Sites like YouTube change often, and an old
    /// yt-dlp is the usual reason a video page stops working.
    /// </summary>
    /// <exception cref="IOException">yt-dlp is in use by a running download.</exception>
    public async Task InstallYtDlpAsync(IProgress<ToolDownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_tools.Folder);
        var file = await FetchVerifiedAsync("yt-dlp", YtDlpRelease + YtDlpAsset, YtDlpRelease + "SHA2-256SUMS", YtDlpAsset, progress, cancellationToken).ConfigureAwait(false);
        File.Move(file.Path, _tools.YtDlpPath, overwrite: true);
        await ForgetAsync(file.Id).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _engine?.Dispose(); // pauses a running tool download; it continues next time
        _http.Dispose();
    }

    /// <summary>
    /// The SHA-256 for <paramref name="fileName"/> in a checksum file: the hash on the line naming
    /// the file (<c>sha256sum</c> style), or the only hash in the file (one file per checksum).
    /// </summary>
    public static string? FindSha256(string checksums, string fileName)
    {
        ArgumentNullException.ThrowIfNull(checksums);
        ArgumentNullException.ThrowIfNull(fileName);
        foreach (var line in checksums.Split('\n'))
        {
            var match = Sha256().Match(line);
            if (match.Success && Regex.IsMatch(line, @"(^|[\s*/])" + Regex.Escape(fileName) + @"\s*$", RegexOptions.IgnoreCase))
            {
                return match.Value.ToLowerInvariant();
            }
        }

        var all = Sha256().Matches(checksums);
        return all.Count == 1 ? all[0].Value.ToLowerInvariant() : null;
    }

    private DownloadEngine Engine
    {
        get
        {
            lock (_engineLock)
            {
                if (_engine is null)
                {
                    _store = new ToolDownloadStore(Path.Combine(DownloadsFolder, "state.json"));
                    _engine = new DownloadEngine(_store, () => DownloadOptions, loggerFactory: _loggerFactory);
                    _engine.RecoverInterrupted(); // after a restart: paused, ready to continue
                }

                return _engine;
            }
        }
    }

    /// <summary>Downloads (or continues) one release file, then checks it against the release's checksum.</summary>
    private async Task<(long Id, string Path)> FetchVerifiedAsync(string tool, string url, string checksumsUrl, string assetName, IProgress<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        var engine = Engine;
        var item = _store!.List().FirstOrDefault(d => d.Url == url);
        if (item is { Status: DownloadStatus.Completed } && !File.Exists(item.DestPath))
        {
            await engine.DeleteAsync(item.Id, deleteFile: false).ConfigureAwait(false);
            item = null;
        }

        item ??= await engine.AddAsync(new DownloadRequest { Url = new Uri(url), SaveFolder = DownloadsFolder, FileName = assetName }, cancellationToken: ct).ConfigureAwait(false);

        if (item.Status != DownloadStatus.Completed)
        {
            engine.Start(item.Id);
            var id = item.Id;
            using (ct.Register(() => _ = engine.PauseAsync(id)))
            {
                while (engine.IsActive(id))
                {
                    if (engine.GetProgress(id) is { } p)
                    {
                        progress?.Report(new ToolDownloadProgress(tool, p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond));
                    }

                    await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                }
            }

            item = _store.Get(id)!;
            ct.ThrowIfCancellationRequested();
            if (item.Status != DownloadStatus.Completed)
            {
                throw new IOException($"{tool}: {item.ErrorMessage ?? "the download stopped"}. Try again to continue where it stopped.");
            }
        }

        progress?.Report(new ToolDownloadProgress(tool, item.TotalBytes ?? 0, item.TotalBytes, 0));
        var sums = await _http.GetStringAsync(new Uri(checksumsUrl), ct).ConfigureAwait(false);
        var expected = FindSha256(sums, assetName) ?? throw new InvalidDataException($"The {tool} release lists no checksum for {assetName}.");
        string actual;
        var stream = File.OpenRead(item.DestPath);
        await using (stream.ConfigureAwait(false))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }

        if (actual != expected)
        {
            await engine.DeleteAsync(item.Id, deleteFile: true).ConfigureAwait(false);
            throw new InvalidDataException($"The downloaded {tool} does not match its published checksum, so it was not installed. Try again.");
        }

        return (item.Id, item.DestPath);
    }

    /// <summary>Removes the record and the downloaded file once it is installed.</summary>
    private async Task ForgetAsync(long id) => await Engine.DeleteAsync(id, deleteFile: true).ConfigureAwait(false);

    /// <summary>Copies the chosen entries into <paramref name="folder"/> by file name only, so no entry can write outside it.</summary>
    private static void ExtractFiles(string zipPath, Func<ZipArchiveEntry, bool> include, string folder)
    {
        Directory.CreateDirectory(folder);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries.Where(include))
        {
            var target = Path.Combine(folder, Path.GetFileName(entry.Name));
            var temp = target + ".download";
            entry.ExtractToFile(temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
    }

    [GeneratedRegex("\\b[0-9a-fA-F]{64}\\b")]
    private static partial Regex Sha256();
}
