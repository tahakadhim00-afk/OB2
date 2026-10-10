using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace OB2.Shared.Ipc;

/// <summary>
/// Native Messaging registration (§8.2): <c>host-manifest.json</c> next to host.exe and a
/// per-user registry value pointing at it, for Chrome and Edge (decision D-65). No admin
/// rights needed (AC-9). All keys are written and removed together.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeHostRegistration
{
    public const string ChromeKeyPath = @"Software\Google\Chrome\NativeMessagingHosts\" + ProtocolInfo.HostName;
    public const string EdgeKeyPath = @"Software\Microsoft\Edge\NativeMessagingHosts\" + ProtocolInfo.HostName;
    public const string ManifestFileName = "host-manifest.json";
    public const string HostExeName = "host.exe";

    public const string ExtensionId = ProtocolInfo.ExtensionId;

    /// <summary>Chrome first: it decides what "registered" means when the keys disagree.</summary>
    public static readonly IReadOnlyList<string> BrowserKeyPaths = [ChromeKeyPath, EdgeKeyPath];

    private readonly IReadOnlyList<string> _keyPaths;
    private readonly string _manifestFolder;

    /// <param name="hostFolder">Folder containing host.exe.</param>
    /// <param name="manifestFolder">Where to write the manifest; the host folder unless it is read-only.</param>
    /// <param name="keyPaths">HKCU subkeys; tests pass scratch keys so the browsers' real keys are untouched.</param>
    public NativeHostRegistration(string hostFolder, string manifestFolder, IReadOnlyList<string>? keyPaths = null)
    {
        HostPath = Path.Combine(Path.GetFullPath(hostFolder), HostExeName);
        _manifestFolder = Path.GetFullPath(manifestFolder);
        _keyPaths = keyPaths is { Count: > 0 } ? keyPaths : BrowserKeyPaths;
    }

    public string HostPath { get; }

    public string ManifestPath => Path.Combine(_manifestFolder, ManifestFileName);

    public bool HostExists => File.Exists(HostPath);

    /// <summary>The manifest path the first registered key points at, or null when none is registered.</summary>
    public string? RegisteredManifestPath() => _keyPaths.Select(ManifestPathIn).FirstOrDefault(p => p is not null);

    public bool IsRegistered => RegisteredManifestPath() is not null;

    /// <summary>
    /// §12.6: a registered key points at a manifest or host.exe that is gone (the folder was
    /// moved), or this copy is registered for some browsers but not all (for example a
    /// registration made before Edge support). A registration that points at another copy which
    /// still exists is left alone, so two copies on one PC do not take it from each other on
    /// every start (decision D-55); the Register button moves it explicitly.
    /// </summary>
    public bool NeedsRepair()
    {
        var registered = _keyPaths.Select(ManifestPathIn).ToList();
        if (registered.All(p => p is null))
        {
            return false;
        }

        if (registered.Any(p => p is not null && IsBroken(p)))
        {
            return true;
        }

        // Complete a partial registration of this copy, but never take a key from another copy.
        return registered.Any(p => p is null) && registered.All(p => p is null || IsThisManifest(p));
    }

    /// <summary>True when every browser key points at this copy's manifest.</summary>
    public bool IsRegisteredHere => _keyPaths.All(k => ManifestPathIn(k) is { } p && IsThisManifest(p));

    public void Register()
    {
        Directory.CreateDirectory(_manifestFolder);
        // Edge uses the same origin scheme for extensions as Chrome.
        var manifest = new HostManifest(ProtocolInfo.HostName, "OB2 download manager bridge", HostPath, "stdio", [$"chrome-extension://{ExtensionId}/"]);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, HostManifestJsonContext.Default.HostManifest));
        foreach (var keyPath in _keyPaths)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key.SetValue(null, ManifestPath, RegistryValueKind.String);
        }
    }

    public void Unregister()
    {
        foreach (var keyPath in _keyPaths)
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }

        try
        {
            File.Delete(ManifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? ManifestPathIn(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(null) as string;
    }

    private bool IsThisManifest(string path) => string.Equals(Path.GetFullPath(path), ManifestPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsBroken(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var path = document.RootElement.GetProperty("path").GetString();
            return string.IsNullOrEmpty(path) || !File.Exists(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
        {
            return true;
        }
    }
}

/// <summary>The Native Messaging host manifest Chrome and Edge read (§8.2).</summary>
internal sealed record HostManifest(string Name, string Description, string Path, string Type, string[] AllowedOrigins);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HostManifest))]
internal sealed partial class HostManifestJsonContext : JsonSerializerContext;
