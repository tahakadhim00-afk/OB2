using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OB2.Storage.Paths;

/// <summary>
/// FR-31 "Start with Windows": a per-user Run value that starts OB2 in the tray at sign-in.
/// HKCU only, so no admin rights. Like the browser registration (decision D-55), a value that
/// points at another copy of OB2 that still exists is left alone unless the user takes it over.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "OB2";

    private readonly string _keyPath;

    /// <param name="exePath">This copy's OB2.exe.</param>
    /// <param name="dataRoot">Only when the app was started with <c>--data</c>; normal starts resolve the folder themselves.</param>
    /// <param name="keyPath">HKCU subkey; tests pass a scratch key.</param>
    public StartupRegistration(string exePath, string? dataRoot = null, string keyPath = RunKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ExePath = Path.GetFullPath(exePath);
        _keyPath = keyPath;
        Command = dataRoot is null
            ? $"\"{ExePath}\" --minimized"
            : $"\"{ExePath}\" --minimized --data \"{Path.GetFullPath(dataRoot).TrimEnd('\\')}\"";
    }

    public string ExePath { get; }

    /// <summary>The exact value written under the Run key.</summary>
    public string Command { get; }

    public string? RegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(ValueName) as string;
    }

    public bool IsEnabledHere => string.Equals(RegisteredCommand(), Command, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the Run value match the setting. <paramref name="takeOver"/> is true when the user
    /// just saved the option, so this copy replaces another copy's value; on start-up it is false.
    /// </summary>
    /// <returns>True when the registry was changed.</returns>
    public bool Sync(bool enabled, bool takeOver)
    {
        var registered = RegisteredCommand();
        var pointsHere = registered is not null && string.Equals(ExecutableOf(registered), ExePath, StringComparison.OrdinalIgnoreCase);

        if (enabled)
        {
            if (string.Equals(registered, Command, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Another working copy owns the value: keep it unless the user asked for this copy.
            if (registered is not null && !pointsHere && !takeOver && ExecutableOf(registered) is { } other && File.Exists(other))
            {
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
            return true;
        }

        if (registered is null || (!pointsHere && !takeOver))
        {
            return false;
        }

        using (var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
        {
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        return true;
    }

    /// <summary>The program path from a Run command line: the quoted part, or up to the first space.</summary>
    public static string? ExecutableOf(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? text : text[..space];
    }
}
