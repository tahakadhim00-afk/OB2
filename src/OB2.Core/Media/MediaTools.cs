namespace OB2.Core.Media;

/// <summary>
/// The external tools for video pages (D-71), all in one folder: yt-dlp (the extractor),
/// Deno (yt-dlp needs a JavaScript runtime for YouTube) and ffmpeg (merges video and audio).
/// </summary>
public sealed class MediaTools
{
    public MediaTools(string folder, IProcessRunner? runner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = Path.GetFullPath(folder);
        Runner = runner ?? ProcessRunner.Instance;
    }

    public string Folder { get; }

    public IProcessRunner Runner { get; }

    public string YtDlpPath => Path.Combine(Folder, "yt-dlp.exe");

    public string DenoPath => Path.Combine(Folder, "deno.exe");

    public string FfmpegFolder => Path.Combine(Folder, "ffmpeg");

    public string FfmpegPath => Path.Combine(FfmpegFolder, "ffmpeg.exe");

    public string CacheFolder => Path.Combine(Folder, "cache");

    public bool IsInstalled => File.Exists(YtDlpPath) && File.Exists(DenoPath) && File.Exists(FfmpegPath);

    /// <summary>
    /// Arguments for every yt-dlp run: OB2's own tools and cache, and no user config file, which
    /// could otherwise change where files go.
    /// </summary>
    public IReadOnlyList<string> CommonArguments =>
    [
        "--ignore-config",
        "--no-colors",
        "--ffmpeg-location", FfmpegFolder,
        "--js-runtimes", "deno:" + DenoPath,
        "--cache-dir", CacheFolder,
    ];
}
