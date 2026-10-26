namespace OB2.Integration.Tests.Infrastructure;

/// <summary>
/// D-80: a test that needs the real ffmpeg, to write a stream and to join one. It looks in the
/// folder <c>OB2_FFMPEG</c> names, then in the portable copy's tools under <c>publish/</c>, and is
/// skipped when neither has <c>ffmpeg.exe</c> and <c>ffprobe.exe</c>.
/// </summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (Folder is null)
        {
            Skip = "Needs ffmpeg: set OB2_FFMPEG to a folder with ffmpeg.exe and ffprobe.exe, or install the video tools in publish/OB2-portable.";
        }
    }

    /// <summary>The folder with ffmpeg.exe and ffprobe.exe; null when there is none.</summary>
    public static string? Folder { get; } = Find();

    private static string? Find()
    {
        static bool Has(string? folder) => folder is not null && File.Exists(Path.Combine(folder, "ffmpeg.exe")) && File.Exists(Path.Combine(folder, "ffprobe.exe"));

        var configured = Environment.GetEnvironmentVariable("OB2_FFMPEG");
        if (Has(configured))
        {
            return configured;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OB2.sln")))
            {
                var portable = Path.Combine(dir.FullName, "publish", "OB2-portable", "tools", "ffmpeg");
                return Has(portable) ? portable : null;
            }
        }

        return null;
    }
}
