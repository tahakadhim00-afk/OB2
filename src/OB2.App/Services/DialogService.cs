using System.Diagnostics;
using System.IO;
using System.Windows;
using OB2.App.Infrastructure;
using OB2.App.ViewModels;
using OB2.App.Views;
using OB2.Core.Downloads;
using OB2.Core.Media;
using OB2.Core.Organization;
using OB2.Core.Services;
using OB2.Core.Settings;
using OB2.Storage.Settings;

namespace OB2.App.Services;

/// <summary>Creates the app's windows and runs shell actions (§9).</summary>
public sealed class DialogService : IDialogService
{
    /// <summary>§11.1: opening these from the internet needs a warning.</summary>
    private static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".scr", ".com", ".vbs", ".js", ".jse", ".wsf", ".hta", ".lnk", ".msix", ".appx",
    };

    private readonly DownloadService _service;
    private readonly AppSettings _settings;
    private readonly JsonSettingsStore _store;
    private readonly ICategoryRepository _categories;
    private readonly IQueueRepository _queues;

    private readonly OB2.Shared.Ipc.NativeHostRegistration _registration;
    private readonly OB2.Storage.Paths.StartupRegistration _startup;
    private readonly MediaInspector _inspector;
    private readonly MediaToolInstaller _installer;

    public DialogService(DownloadService service, AppSettings settings, JsonSettingsStore store, ICategoryRepository categories, IQueueRepository queues, OB2.Shared.Ipc.NativeHostRegistration registration, OB2.Storage.Paths.StartupRegistration startup, MediaInspector inspector, MediaToolInstaller installer)
    {
        _inspector = inspector;
        _installer = installer;
        _registration = registration;
        _startup = startup;
        _service = service;
        _settings = settings;
        _store = store;
        _categories = categories;
        _queues = queues;
    }

    private static Window? Owner => Application.Current.MainWindow is { IsVisible: true } w ? w : null;

    public DownloadItem? ShowAddDownload(AddDownloadPrefill prefill)
    {
        ArgumentNullException.ThrowIfNull(prefill);
        if (MediaPageOf(prefill) is { } page)
        {
            return ShowMediaDownload(prefill, page);
        }

        var window = new AddDownloadWindow(new AddDownloadViewModel(_service, _settings, prefill));
        Place(window, prefill);
        window.ShowDialog();

        // "Choose quality…" on a video address typed into the Add window.
        return window.SwitchToVideo is { } video ? ShowMediaDownload(prefill with { Url = video.AbsoluteUri, MediaPage = true }, video) : window.Result;
    }

    /// <summary>D-71: the Download Video window, for YouTube and pages the extension marks as video pages.</summary>
    private DownloadItem? ShowMediaDownload(AddDownloadPrefill prefill, Uri page)
    {
        var window = new MediaDownloadWindow(new MediaDownloadViewModel(_service, _inspector, _installer, prefill, page));
        Place(window, prefill);
        window.ShowDialog();
        return window.Result;
    }

    private static Uri? MediaPageOf(AddDownloadPrefill prefill)
    {
        if (!Uri.TryCreate(prefill.Url?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return VideoSites.YouTubeVideo(url) ?? (prefill.MediaPage ? url : null);
    }

    private static void Place(Window window, AddDownloadPrefill prefill)
    {
        if (Owner is { } owner)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Topmost = prefill.Source == DownloadSource.Chrome;
        }
    }

    public bool? ConfirmDelete(int count, bool anyCompleted)
    {
        var window = ConfirmWindow.ForDelete(count, anyCompleted);
        window.Owner = Owner;
        return window.ShowDialog() == true ? window.OptionChecked : null;
    }

    public void ShowSettings()
    {
        var window = new SettingsWindow(new SettingsViewModel(_settings, _store, _categories, _service.Engine, _service.DefaultSaveFolder, _registration, _startup)) { Owner = Owner };
        if (window.Owner is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
    }

    public void ShowScheduler(long? queueId)
    {
        var window = new SchedulerWindow(new SchedulerViewModel(_service, _queues, queueId)) { Owner = Owner };
        window.ShowDialog();
    }

    public void ShowComplete(DownloadItem item)
    {
        var window = new CompleteWindow(item, () => OpenFile(item.DestPath), () => OpenFolder(item.DestPath));
        window.Closed += async (_, _) =>
        {
            var category = _service.Categories.FirstOrDefault(c => c.Id == item.CategoryId)?.Name ?? string.Empty;
            if (window.SuppressCategory && !_settings.General.CompleteDialogHiddenCategories.Contains(category))
            {
                _settings.General.CompleteDialogHiddenCategories.Add(category);
                await _store.SaveAsync(_settings);
            }
        };
        window.Show();
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            OpenFolder(path);
            return;
        }

        if (RiskyExtensions.Contains(Path.GetExtension(path))
            && MessageBox.Show(Owner ?? Application.Current.MainWindow!, Loc.Format("Exec_Message", Path.GetFileName(path)), Loc.Get("Exec_Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No associated app, or the user cancelled Windows' "open with" prompt: show the file instead.
            OpenFolder(path);
        }
    }

    public void OpenFolder(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } })?.Dispose();
        }
        else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
    }

    public void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard held by another app; nothing useful to do.
        }
    }
}
