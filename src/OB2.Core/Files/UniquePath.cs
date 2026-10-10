using System.Globalization;
using OB2.Core.Downloads;

namespace OB2.Core.Files;

/// <summary>Auto-rename on collision (FR-7): <c>file.zip</c> → <c>file (1).zip</c> → <c>file (2).zip</c>.</summary>
public static class UniquePath
{
    /// <param name="path">The preferred full path.</param>
    /// <param name="isTaken">Extra check, e.g. another download already targets that path.</param>
    public static string Allocate(string path, Func<string, bool>? isTaken = null)
    {
        if (IsFree(path, isTaken))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var extension = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);

        for (var n = 1; n < 10_000; n++)
        {
            var candidate = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){extension}"));
            if (IsFree(candidate, isTaken))
            {
                return candidate;
            }
        }

        throw new IOException($"No free file name near '{path}'.");
    }

    private static bool IsFree(string path, Func<string, bool>? isTaken) =>
        !File.Exists(path)
        && !Directory.Exists(path)
        && !File.Exists(path + DownloadItem.PartSuffix)
        && !Directory.Exists(path + ".parts") // a media download's parts (D-71)
        && (isTaken is null || !isTaken(path));
}
