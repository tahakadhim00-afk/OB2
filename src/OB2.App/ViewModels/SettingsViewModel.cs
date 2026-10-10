using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OB2.App.Infrastructure;
using OB2.Core.Engine;
using OB2.Core.Organization;
using OB2.Core.Settings;
using OB2.Shared.Ipc;
using OB2.Storage.Settings;
using System.IO;

namespace OB2.App.ViewModels;

/// <summary>Options window (FR-29, FR-30): defaults, connection settings, categories.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly JsonSettingsStore _store;
    private readonly ICategoryRepository _categories;
    private readonly DownloadEngine _engine;
    private readonly List<CategoryEditViewModel> _removed = [];

    [ObservableProperty]
    private string _defaultFolder;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _showCompleteDialog;

    [ObservableProperty]
    private bool _showNotifications;

    [ObservableProperty]
    private bool _autoResume;

    [ObservableProperty]
    private int _connections;

    [ObservableProperty]
    private int _maxPerHost;

    [ObservableProperty]
    private int _retries;

    [ObservableProperty]
    private int _connectTimeout;

    [ObservableProperty]
    private int _readTimeout;

    [ObservableProperty]
    private string _userAgent;

    [ObservableProperty]
    private int _globalLimitKBps;

    [ObservableProperty]
    private CategoryEditViewModel? _selectedCategory;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _confirmCapture;

    [ObservableProperty]
    private double _captureMinSizeMb;

    [ObservableProperty]
    private string _captureExtensions;

    [ObservableProperty]
    private string _excludedDomains;

    [ObservableProperty]
    private string _browserStatus = string.Empty;

    private readonly NativeHostRegistration? _registration;

    public SettingsViewModel(AppSettings settings, JsonSettingsStore store, ICategoryRepository categories, DownloadEngine engine, string effectiveDefaultFolder, NativeHostRegistration? registration = null)
    {
        _settings = settings;
        _store = store;
        _categories = categories;
        _engine = engine;
        _registration = registration;
        _confirmCapture = settings.Capture.ConfirmBeforeAdding;
        _captureMinSizeMb = Math.Round(settings.Capture.MinSizeBytes / 1048576.0, 2);
        _captureExtensions = string.Join(", ", settings.Capture.Extensions);
        _excludedDomains = string.Join(", ", settings.Capture.ExcludedDomains);
        UpdateBrowserStatus();

        _defaultFolder = settings.Folders.DefaultSaveFolder ?? effectiveDefaultFolder;
        _minimizeToTray = settings.General.MinimizeToTrayOnClose;
        _showCompleteDialog = settings.General.ShowCompleteDialog;
        _showNotifications = settings.General.ShowNotifications;
        _autoResume = settings.Downloads.AutoResumeOnStartup;
        _connections = settings.Downloads.Connections;
        _maxPerHost = settings.Downloads.MaxConnectionsPerHost;
        _retries = settings.Downloads.MaxRetries;
        _connectTimeout = settings.Downloads.ConnectTimeoutSeconds;
        _readTimeout = settings.Downloads.ReadIdleTimeoutSeconds;
        _userAgent = settings.Downloads.UserAgent;
        _globalLimitKBps = (int)((settings.SpeedLimits.GlobalBytesPerSecond ?? 0) / 1024);

        foreach (var category in categories.List())
        {
            Categories.Add(new CategoryEditViewModel(category));
        }
    }

    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<CategoryEditViewModel> Categories { get; } = [];

    [RelayCommand]
    private void ResetUserAgent() => UserAgent = DownloadSettings.DefaultUserAgent;

    [RelayCommand]
    private void AddCategory()
    {
        var category = new CategoryEditViewModel(new Category { Name = Loc.Get("Settings_NewCategory") });
        Categories.Add(category);
        SelectedCategory = category;
    }

    [RelayCommand]
    private void RemoveCategory()
    {
        if (SelectedCategory is { } category)
        {
            Categories.Remove(category);
            if (category.Source.Id != 0)
            {
                _removed.Add(category);
            }
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var names = Categories.Select(c => c.Name.Trim()).ToList();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
        {
            ErrorText = Loc.Get("Settings_CategoryNamesInvalid");
            return;
        }

        var d = _settings.Downloads;
        _settings.Folders.DefaultSaveFolder = string.IsNullOrWhiteSpace(DefaultFolder) ? null : DefaultFolder.Trim();
        _settings.General.MinimizeToTrayOnClose = MinimizeToTray;
        _settings.General.ShowCompleteDialog = ShowCompleteDialog;
        _settings.General.ShowNotifications = ShowNotifications;
        d.AutoResumeOnStartup = AutoResume;
        d.Connections = Connections;
        d.MaxConnectionsPerHost = MaxPerHost;
        d.MaxRetries = Retries;
        d.ConnectTimeoutSeconds = ConnectTimeout;
        d.ReadIdleTimeoutSeconds = ReadTimeout;
        d.UserAgent = UserAgent;
        _settings.SpeedLimits.GlobalBytesPerSecond = GlobalLimitKBps > 0 ? GlobalLimitKBps * 1024L : null;
        _settings.Capture.ConfirmBeforeAdding = ConfirmCapture;
        _settings.Capture.MinSizeBytes = (long)(Math.Max(0, CaptureMinSizeMb) * 1048576);
        _settings.Capture.Extensions = Category.ParseExtensions(CaptureExtensions);
        _settings.Capture.ExcludedDomains = [.. ExcludedDomains.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => d.ToLowerInvariant())];
        SettingsValidator.Normalize(_settings);
        await _store.SaveAsync(_settings);
        _engine.GlobalLimiter.BytesPerSecond = _settings.SpeedLimits.GlobalBytesPerSecond;

        foreach (var removed in _removed)
        {
            _categories.Delete(removed.Source.Id);
        }

        foreach (var edit in Categories)
        {
            edit.Apply();
            if (edit.Source.Id == 0)
            {
                _categories.Insert(edit.Source);
            }
            else
            {
                _categories.Update(edit.Source);
            }
        }

        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>§8.2: writes host-manifest.json and the HKCU key. No admin rights needed (AC-9).</summary>
    [RelayCommand]
    private void RegisterBrowser()
    {
        try
        {
            _registration?.Register();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorText = ex.Message;
        }

        UpdateBrowserStatus();
    }

    [RelayCommand]
    private void UnregisterBrowser()
    {
        _registration?.Unregister();
        UpdateBrowserStatus();
    }

    private void UpdateBrowserStatus()
    {
        if (_registration is null)
        {
            return;
        }

        BrowserStatus = !_registration.HostExists
            ? Loc.Get("Browser_NoHost")
            : !_registration.IsRegistered
                ? Loc.Get("Browser_NotRegistered")
                : _registration.NeedsRepair()
                    ? Loc.Get("Browser_NeedsRepair")
                    : _registration.IsRegisteredHere
                        ? Loc.Format("Browser_Registered", NativeHostRegistration.ExtensionId)
                        : Loc.Get("Browser_OtherCopy");
    }
}

public sealed partial class CategoryEditViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _extensions;

    [ObservableProperty]
    private string _folder;

    public CategoryEditViewModel(Category source)
    {
        Source = source;
        _name = source.Name;
        _extensions = string.Join(", ", source.Extensions);
        _folder = source.SaveFolder ?? string.Empty;
    }

    public Category Source { get; }

    public void Apply()
    {
        Source.Name = Name.Trim();
        Source.Extensions = Category.ParseExtensions(Extensions);
        Source.SaveFolder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim();
    }
}
