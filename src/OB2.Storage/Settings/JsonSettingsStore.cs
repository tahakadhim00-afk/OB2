using System.Text.Json;
using System.Text.Json.Serialization;
using OB2.Core.IO;
using OB2.Core.Settings;

namespace OB2.Storage.Settings;

/// <summary>Outcome of loading settings; <see cref="Issues"/> is logged once logging is up.</summary>
public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Issues);

/// <summary>
/// Loads and saves <c>settings.json</c>. Saves are atomic (§11.3). A missing file yields
/// defaults; an unreadable file is preserved as <c>settings.json.corrupt-*</c> and replaced
/// by defaults rather than stopping the app.
/// </summary>
public sealed class JsonSettingsStore
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public JsonSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string FilePath => _path;

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        AppSettings? settings = null;

        if (File.Exists(_path))
        {
            try
            {
                await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
                if (settings is null)
                {
                    issues.Add("settings.json contained null; using defaults");
                }
            }
            catch (JsonException ex)
            {
                var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Copy(_path, backup, overwrite: true);
                issues.Add($"settings.json could not be parsed ({ex.Message}); saved a copy as {Path.GetFileName(backup)} and using defaults");
            }
        }

        var needsSave = settings is null;
        settings ??= new AppSettings();

        if (settings.Version > AppSettings.CurrentVersion)
        {
            issues.Add($"settings.json version {settings.Version} is newer than this app supports ({AppSettings.CurrentVersion}); unknown values are ignored");
        }

        var changes = SettingsValidator.Normalize(settings);
        issues.AddRange(changes);

        if (needsSave)
        {
            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        return new SettingsLoadResult(settings, issues);
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);
        return AtomicFile.WriteAllBytesAsync(_path, bytes, cancellationToken);
    }
}
