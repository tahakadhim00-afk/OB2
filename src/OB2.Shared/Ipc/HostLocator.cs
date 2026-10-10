using System.Text.Json;

namespace OB2.Shared.Ipc;

/// <summary>
/// How host.exe finds the app beside it and the pipe name in its settings, without
/// referencing the storage layer. Mirrors the §10.2 rule: <c>portable.flag</c> beside the
/// executables means settings beside them, otherwise <c>%APPDATA%\OB2</c>.
/// </summary>
public static class HostLocator
{
    public const string AppExeName = "OB2.exe";

    public static string AppExePath(string baseDirectory) => Path.Combine(baseDirectory, AppExeName);

    public static IEnumerable<string> SettingsCandidates(string baseDirectory, string appDataDirectory)
    {
        if (File.Exists(Path.Combine(baseDirectory, "portable.flag")))
        {
            yield return Path.Combine(baseDirectory, "settings.json");
        }

        // Also covers a portable copy on a read-only folder, which falls back to %APPDATA%.
        yield return Path.Combine(appDataDirectory, "OB2", "settings.json");
    }

    /// <returns>The pipe name from the first settings file that has one, or null.</returns>
    public static string? ReadPipeName(string baseDirectory, string appDataDirectory)
    {
        foreach (var file in SettingsCandidates(baseDirectory, appDataDirectory))
        {
            try
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (document.RootElement.TryGetProperty("ipc", out var ipc)
                    && ipc.TryGetProperty("pipeName", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } pipe)
                {
                    return pipe;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}
