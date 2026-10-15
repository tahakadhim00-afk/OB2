using OB2.Core.Downloads;

namespace OB2.Core.Files;

/// <summary>A <c>.pgpart</c> file on disk that no download row claims (§10.3).</summary>
public sealed record OrphanFile(string PartPath, long SizeBytes, DateTimeOffset LastWriteUtc)
{
    /// <summary>The name the file would take if it finished, i.e. without the suffix.</summary>
    public string FileName => Path.GetFileName(PartPath[..^DownloadItem.PartSuffix.Length]);

    public string Folder => Path.GetDirectoryName(PartPath) ?? string.Empty;
}

/// <summary>
/// Finds partial files left behind by a crash or a deleted row (§10.3). The scan only reports:
/// an orphan is never deleted or resumed automatically, because the file may be the only copy
/// of a long download and OB2 cannot know what the user wants done with it.
/// </summary>
public sealed class OrphanScanner
{
    private readonly IDownloadRepository _downloads;

    public OrphanScanner(IDownloadRepository downloads) =>
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));

    /// <summary>
    /// Scans <paramref name="folders"/> (one level, no recursion) for <c>.pgpart</c> files that no
    /// download claims. Folders that are missing or unreadable are skipped rather than failing the
    /// whole scan, so one bad path cannot hide the orphans everywhere else.
    /// </summary>
    public IReadOnlyList<OrphanFile> Scan(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        // Every part path a row owns, whatever its status: a paused download's file is not an
        // orphan, and neither is a failed one whose .pgpart was deliberately kept.
        var claimed = _downloads.List()
            .Select(d => d.PartPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = new List<OrphanFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            foreach (var path in SafeEnumerate(folder))
            {
                if (claimed.Contains(path) || !seen.Add(path))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        orphans.Add(new OrphanFile(path, info.Length, info.LastWriteTimeUtc));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Vanished or locked between listing and reading; nothing to report.
                }
            }
        }

        return [.. orphans.OrderByDescending(o => o.LastWriteUtc)];
    }

    /// <summary>Deletes one orphan, on an explicit request only (§10.3 forbids doing it silently).</summary>
    public static void Delete(string partPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partPath);
        if (!partPath.EndsWith(DownloadItem.PartSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Only {DownloadItem.PartSuffix} files can be removed this way.", nameof(partPath));
        }

        File.Delete(partPath);
    }

    /// <summary>
    /// Lists part files without throwing. Enumeration is lazy, so a folder that disappears or
    /// denies access part way through would throw while being iterated, not when the enumerable
    /// is created: the whole listing is therefore materialised inside the try.
    /// </summary>
    private static IReadOnlyList<string> SafeEnumerate(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? [.. Directory.EnumerateFiles(folder, "*" + DownloadItem.PartSuffix, SearchOption.TopDirectoryOnly)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
