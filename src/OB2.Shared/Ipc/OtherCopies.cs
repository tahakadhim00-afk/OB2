using System.ComponentModel;
using System.Diagnostics;

namespace OB2.Shared.Ipc;

/// <summary>An OB2 running from another folder, and the pipe it answers on (D-92).</summary>
public sealed record RunningCopy(string Folder, string PipeName);

/// <summary>
/// D-92: finds an OB2 already running from another folder, so a copy opened by hand can hand
/// over to it instead of starting a second app with its own tray icon, settings and download
/// list. Copies that share a data folder are not "other": the instance lock and the hand-over
/// of §8.5 (D-54) already deal with those.
/// </summary>
public static class OtherCopies
{
    /// <summary>The folders OB2.exe runs from right now, except for this process.</summary>
    public static IReadOnlyList<string> RunningFolders()
    {
        var folders = new List<string>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HostLocator.AppExeName)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    if (Path.GetDirectoryName(process.MainModule?.FileName) is { Length: > 0 } folder)
                    {
                        folders.Add(folder);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Exited meanwhile, or another user's process: not one this user could switch to.
                }
            }
        }

        return folders;
    }

    /// <summary>
    /// The first of <paramref name="runningFolders"/> that is another copy (another folder with
    /// another data folder) and whose app answers on its pipe; null when there is none. A copy
    /// without settings, or running only a command-line download, has no pipe and is passed over.
    /// </summary>
    /// <param name="ownFolder">This copy's folder.</param>
    /// <param name="appDataDirectory">%APPDATA%, where an installed (not portable) copy keeps its settings.</param>
    /// <param name="runningFolders">The folders OB2.exe runs from (<see cref="RunningFolders"/>).</param>
    /// <param name="timeout">How long to wait for each pipe.</param>
    public static async Task<RunningCopy?> FindAsync(string ownFolder, string appDataDirectory, IEnumerable<string> runningFolders, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentNullException.ThrowIfNull(runningFolders);

        var own = Normalize(ownFolder);
        var ownPipe = HostLocator.ReadPipeName(own, appDataDirectory);
        foreach (var folder in runningFolders.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(folder, own, StringComparison.OrdinalIgnoreCase))
            {
                continue; // this copy, opened again
            }

            var pipe = HostLocator.ReadPipeName(folder, appDataDirectory);
            if (pipe is null || string.Equals(pipe, ownPipe, StringComparison.Ordinal))
            {
                continue; // never started, or the same data folder (two installed copies share %APPDATA%\OB2)
            }

            if (await PipeClient.IsListeningAsync(pipe, timeout).ConfigureAwait(false))
            {
                return new RunningCopy(folder, pipe);
            }
        }

        return null;
    }

    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
