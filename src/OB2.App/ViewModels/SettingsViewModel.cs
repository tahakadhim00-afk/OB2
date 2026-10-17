using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OB2.App.Infrastructure;
using OB2.Core.Engine;
using OB2.Core.Organization;
using OB2.Core.Settings;
using OB2.Shared.Ipc;
using OB2.Storage.Paths;
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
    private readonly BandwidthSchedule? _bandwidth;
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

    /// <summary>0 disables retrying after a download has already failed.</summary>
    [ObservableProperty]
    private int _autoRetryAttempts;

    [ObservableProperty]
    private int _autoRetryDelaySeconds;

    [ObservableProperty]
    private bool _windowEnabled;

    [ObservableProperty]
    private string _windowStart = "01:00";

    [ObservableProperty]
    private string _windowStop = "08:00";

    /// <summary>0 means unlimited while the window is open.</summary>
    [ObservableProperty]
    private int _windowLimitKBps;

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

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _rememberAuth;

    /// <summary>Index into <see cref="ThemeChoices"/>; the order matches <see cref="AppTheme"/>.</summary>
    [ObservableProperty]
    private int _themeIndex;

    /// <summary>
    /// Built in the constructor, not in a field initializer: Loc reads the merged Strings
    /// dictionary off Application.Current, which is not reliably there that early, and the
    /// list came out blank.
    /// </summary>
    public IReadOnlyList<string> ThemeChoices { get; }

    /// <summary>
    /// Applied as soon as the user picks one, so the choice can be seen before Save. Cancelling
    /// the window puts the saved theme back (see SettingsWindow).
    /// </summary>
    partial void OnThemeIndexChanged(int value) => ThemeManager.Apply((AppTheme)value);

    private readonly NativeHostRegistration? _registration;
    private readonly StartupRegistration? _startup;

    public SettingsViewModel(AppSettings settings, JsonSettingsStore store, ICategoryRepository categories, DownloadEngine engine, string effectiveDefaultFolder, NativeHostRegistration? registration = null, StartupRegistration? startup = null, BandwidthSchedule? bandwidth = null)
    {
        _settings = settings;
        _store = store;
        _categories = categories;
        _engine = engine;
        _bandwidth = bandwidth;
        _registration = registration;
        _startup = startup;
        _startWithWindows = settings.General.StartWithWindows;
        _rememberAuth = settings.Downloads.RememberAuth;
        ThemeChoices = [Loc.Get("Settings_ThemeLight"), Loc.Get("Settings_ThemeDark")];
        _themeIndex = (int)settings.General.Theme;
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

        _autoRetryAttempts = settings.Downloads.AutoRetryAttempts;
        _autoRetryDelaySeconds = settings.Downloads.AutoRetryDelaySeconds;

        var window = settings.SpeedLimits.Window;
        _windowEnabled = window.Enabled;
        _windowStart = window.Start;
        _windowStop = window.Stop;
        _windowLimitKBps = (int)((window.BytesPerSecond ?? 0) / 1024);
        WindowDays = [.. Enum.GetValues<DayOfWeek>()
            .OrderBy(d => ((int)d + 6) % 7) // Monday first, as in the Scheduler
            .Select(d => new DayChoice(d, CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d),
                window.Days.Count == 0 || window.Days.Contains(d)))];

        foreach (var category in categories.List())
        {
            Categories.Add(new CategoryEditViewModel(category));
        }
    }

    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<CategoryEditViewModel> Categories { get; } = [];

    /// <summary>Weekdays the bandwidth window applies on (FR-9).</summary>
    public IReadOnlyList<DayChoice> WindowDays { get; } = [];

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

        // Rejected here rather than silently ignored later: a window whose times do not parse
        // would never open, and the user would be left wondering why the limit never changes.
        if (WindowEnabled
            && (!TimeOnly.TryParse(WindowStart, CultureInfo.InvariantCulture, out _)
                || (!string.IsNullOrWhiteSpace(WindowStop) && !TimeOnly.TryParse(WindowStop, CultureInfo.InvariantCulture, out _))))
        {
            ErrorText = Loc.Get("Settings_WindowTimeInvalid");
            return;
        }

        // FR-31: only a change made here takes the Run value over from another copy of OB2.
        var startupChanged = StartWithWindows != _settings.General.StartWithWindows;
        try
        {
            _startup?.Sync(StartWithWindows, takeOver: startupChanged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorText = ex.Message;
            return;
        }

        _settings.General.StartWithWindows = StartWithWindows;
        _settings.General.Theme = (AppTheme)ThemeIndex;
        var d = _settings.Downloads;
        var rememberAuthChanged = RememberAuth != d.RememberAuth;
        d.RememberAuth = RememberAuth;
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
        d.AutoRetryAttempts = Math.Max(0, AutoRetryAttempts);
        d.AutoRetryDelaySeconds = Math.Max(1, AutoRetryDelaySeconds);
        _settings.SpeedLimits.GlobalBytesPerSecond = GlobalLimitKBps > 0 ? GlobalLimitKBps * 1024L : null;
        var window = _settings.SpeedLimits.Window;
        window.Enabled = WindowEnabled;
        window.Start = WindowStart;
        window.Stop = WindowStop;
        window.BytesPerSecond = WindowLimitKBps > 0 ? WindowLimitKBps * 1024L : null;
        // Every day selected is stored as an empty list, which the evaluator reads as "any day".
        var days = WindowDays.Where(d => d.IsChecked).Select(d => d.Day).ToList();
        window.Days = days.Count == WindowDays.Count ? [] : days;
        _settings.Capture.ConfirmBeforeAdding = ConfirmCapture;
        _settings.Capture.MinSizeBytes = (long)(Math.Max(0, CaptureMinSizeMb) * 1048576);
        _settings.Capture.Extensions = Category.ParseExtensions(CaptureExtensions);
        _settings.Capture.ExcludedDomains = [.. ExcludedDomains.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => d.ToLowerInvariant())];
        SettingsValidator.Normalize(_settings);
        await _store.SaveAsync(_settings);
        // Through the schedule, so saving while the bandwidth window is open does not replace
        // the window's limit with the plain one until the next tick.
        if (_bandwidth is not null)
        {
            _bandwidth.Apply();
        }
        else
        {
            _engine.GlobalLimiter.BytesPerSecond = _settings.SpeedLimits.GlobalBytesPerSecond;
        }
        if (rememberAuthChanged)
        {
            // §11.1: on saves the cookies of unfinished downloads now; off deletes all saved ones.
            _engine.SyncSavedCookies(RememberAuth);
        }

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
