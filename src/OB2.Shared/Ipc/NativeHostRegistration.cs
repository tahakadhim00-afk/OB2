using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace OB2.Shared.Ipc;

/// <summary>
/// Chrome Native Messaging registration (§8.2): <c>host-manifest.json</c> next to host.exe and
/// a per-user registry value pointing at it. No admin rights needed (AC-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeHostRegistration
{
    public const string ChromeKeyPath = @"Software\Google\Chrome\NativeMessagingHosts\" + ProtocolInfo.HostName;
    public const string ManifestFileName = "host-manifest.json";
    public const string HostExeName = "host.exe";

    public const string ExtensionId = ProtocolInfo.ExtensionId;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string _keyPath;
    private readonly string _manifestFolder;

    /// <param name="hostFolder">Folder containing host.exe.</param>
    /// <param name="manifestFolder">Where to write the manifest; the host folder unless it is read-only.</param>
    /// <param name="keyPath">HKCU subkey; tests pass a scratch key so Chrome's real key is untouched.</param>
    public NativeHostRegistration(string hostFolder, string manifestFolder, string keyPath = ChromeKeyPath)
    {
        HostPath = Path.Combine(Path.GetFullPath(hostFolder), HostExeName);
        _manifestFolder = Path.GetFullPath(manifestFolder);
        _keyPath = keyPath;
    }

    public string HostPath { get; }

    public string ManifestPath => Path.Combine(_manifestFolder, ManifestFileName);

    public bool HostExists => File.Exists(HostPath);

    /// <summary>The manifest path the registry points at, or null when not registered.</summary>
    public string? RegisteredManifestPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(null) as string;
    }

    public bool IsRegistered => RegisteredManifestPath() is not null;

    /// <summary>
    /// §12.6: registered, but the manifest or the host.exe it points at is gone (the folder was
    /// moved). A registration that points at another copy which still exists is left alone, so
    /// two copies on one PC do not take the registration from each other on every start
    /// (decision D-55); the Register button moves it explicitly.
    /// </summary>
    public bool NeedsRepair()
    {
        var registered = RegisteredManifestPath();
        if (registered is null)
        {
            return false;
        }

        if (!File.Exists(registered))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registered));
            var path = document.RootElement.GetProperty("path").GetString();
            return string.IsNullOrEmpty(path) || !File.Exists(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>True when the registry points at this copy's manifest.</summary>
    public bool IsRegisteredHere =>
        RegisteredManifestPath() is { } registered && string.Equals(Path.GetFullPath(registered), ManifestPath, StringComparison.OrdinalIgnoreCase);

    public void Register()
    {
        Directory.CreateDirectory(_manifestFolder);
        var manifest = new
        {
            name = ProtocolInfo.HostName,
            description = "OB2 download manager bridge",
            path = HostPath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" },
        };
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, Indented));
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key.SetValue(null, ManifestPath, RegistryValueKind.String);
    }

    public void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        try
        {
            File.Delete(ManifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
