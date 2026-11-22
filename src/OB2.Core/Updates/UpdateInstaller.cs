using System.Text.Json;
using OB2.Core.IO;

namespace OB2.Core.Updates;

/// <summary>
/// D-85: what an install changed, kept as <c>updates\installed.json</c> in the data root until the
/// new version has started: the files to clean up, the downloads to continue, and whether the
/// browser extension needs reloading. Paths are relative to the program folder.
/// </summary>
public sealed class InstallRecord
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    /// <summary>Files whose previous version now ends in <see cref="UpdateInstaller.OldSuffix"/>.</summary>
    public List<string> Replaced { get; set; } = [];

    /// <summary>Files the update put in place.</summary>
    public List<string> Placed { get; set; } = [];

    /// <summary>The extension's files changed, so the browser must reload it (unpacked extensions never reload themselves).</summary>
    public bool ExtensionChanged { get; set; }

    /// <summary>Downloads paused for the restart, to continue once the new version runs.</summary>
    public List<long> Resume { get; set; } = [];

    public static InstallRecord? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(string path) => AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(this, Json));
}

/// <summary>
/// D-85: replaces the running copy's program files with an unpacked update. Windows lets a running
/// program's file be renamed but not overwritten, so each file is renamed to <c>.old</c> first and
/// the new one copied in; the running OB2 keeps working from the renamed file until it restarts.
/// If any step fails, every file is put back and nothing has changed. Only the program files are
/// touched: <c>portable.flag</c> (a portable copy stays portable, an installed one is not made
/// portable), the settings, the database and the video tools are never in an update.
/// </summary>
public static class UpdateInstaller
{
    public const string AppExe = "OB2.exe";
    public const string OldSuffix = ".old";

    /// <summary>A file of a version that failed to start, moved aside when going back (it may still be running).</summary>
    public const string FailedSuffix = ".failed";

    public const string ExtensionFolder = "chrome-extension";

    /// <summary>Never replaced, even if a release carried them.</summary>
    private static readonly HashSet<string> Kept = new(StringComparer.OrdinalIgnoreCase) { "portable.flag", "settings.json", "host-manifest.json" };

    private static readonly string[] KeptFolders = ["data", "tools", "updates"];

    /// <exception cref="UpdateException">A file could not be replaced; everything was put back.</exception>
    public static InstallRecord Install(string updateFolder, string installFolder, Version from, Version to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var files = RelativeFiles(updateFolder).Where(IsProgramFile).ToList();
        var extension = Path.Combine(installFolder, ExtensionFolder);
        var hadExtension = Directory.Exists(extension);
        var newExtensionFiles = files.Where(IsExtensionFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = hadExtension
            ? RelativeFiles(extension).Select(f => Path.Combine(ExtensionFolder, f))
                .Where(f => !newExtensionFiles.Contains(f) && !f.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase) && !f.EndsWith(FailedSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

        var record = new InstallRecord
        {
            From = ReleaseVersion.Text(from),
            To = ReleaseVersion.Text(to),
            // A copy that never had the extension folder does not use it; nothing to reload.
            ExtensionChanged = hadExtension && (stale.Count > 0 || newExtensionFiles.Any(f => !SameContent(Path.Combine(installFolder, f), Path.Combine(updateFolder, f)))),
        };

        try
        {
            foreach (var file in files)
            {
                var target = Path.Combine(installFolder, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    MoveAside(target, OldSuffix);
                    record.Replaced.Add(file);
                }

                File.Copy(Path.Combine(updateFolder, file), target);
                record.Placed.Add(file);
            }

            foreach (var file in stale)
            {
                MoveAside(Path.Combine(installFolder, file), OldSuffix);
                record.Replaced.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Undo(installFolder, record);
            throw new UpdateException($"OB2 could not replace its files, so nothing was changed: {ex.Message}", ex);
        }

        return record;
    }

    /// <summary>
    /// Puts the previous version back: the update's files are removed (or, while still running,
    /// moved aside as <see cref="FailedSuffix"/>) and each <c>.old</c> file takes its name again.
    /// </summary>
    public static void Undo(string installFolder, InstallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        foreach (var file in Enumerable.Reverse(record.Placed))
        {
            var path = Path.Combine(installFolder, file);
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The running OB2.exe can't be deleted, but it can be renamed.
                MoveAside(path, FailedSuffix);
            }
        }

        foreach (var file in Enumerable.Reverse(record.Replaced))
        {
            var path = Path.Combine(installFolder, file);
            if (File.Exists(path + OldSuffix))
            {
                File.Move(path + OldSuffix, path, overwrite: true);
            }
        }
    }

    /// <summary>
    /// After the new version started: deletes the previous version's files and anything a going
    /// back left aside. Returns what could not be deleted yet (still in use), to try next time.
    /// </summary>
    public static IReadOnlyList<string> RemoveLeftovers(string installFolder, InstallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var left = new List<string>();
        foreach (var path in record.Replaced.Select(f => f + OldSuffix).Concat(record.Placed.Select(f => f + FailedSuffix)))
        {
            try
            {
                File.Delete(Path.Combine(installFolder, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(path);
            }
        }

        return left;
    }

    /// <summary>The files of the update that belong to the program (not settings, data, or the copy's mode).</summary>
    private static bool IsProgramFile(string relative) =>
        !Kept.Contains(relative)
        && !KeptFolders.Any(folder => relative.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static bool IsExtensionFile(string relative) =>
        relative.StartsWith(ExtensionFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void MoveAside(string path, string suffix)
    {
        var aside = path + suffix;
        if (File.Exists(aside))
        {
            File.Delete(aside); // left by an earlier update
        }

        File.Move(path, aside);
    }

    private static IEnumerable<string> RelativeFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f));

    private static bool SameContent(string a, string b)
    {
        var first = new FileInfo(a);
        var second = new FileInfo(b);
        return first.Exists && second.Exists && first.Length == second.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
