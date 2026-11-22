using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OB2.App.Infrastructure;
using OB2.Core.Downloads;
using OB2.Core.Services;
using OB2.Core.Updates;
using OB2.Storage.Paths;

namespace OB2.App.Services;

public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Installing,
    Failed,
    Updated,
}

/// <summary>
/// D-85: the main window's update bar and everything behind it. GitHub is asked for the latest
/// release only when the user clicks the toolbar's "Updates" (never on its own: the owner's choice);
/// a newer one shows the bar. "Update now" downloads and checks the release (UpdateDownloader),
/// pauses running downloads, swaps the program files (UpdateInstaller), and starts the new
/// OB2.exe, which waits for this one to exit. The new one then cleans up, continues the paused
/// downloads and says it was updated. Nothing is installed without the click, and a copy that
/// cannot replace its own files only offers the release page.
/// </summary>
public sealed partial class UpdateService : ObservableObject, IDisposable
{
    private static readonly TimeSpan UpToDateShownFor = TimeSpan.FromSeconds(8);

    private readonly AppPaths _paths;
    private readonly DownloadService _downloads;
    private readonly IDialogService _dialogs;
    private readonly TrayService _tray;
    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateChecker _checker;
    private readonly UpdateDownloader _downloader;
    private readonly DispatcherTimer _hideTimer;
    private CancellationTokenSource? _cancel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateNow), nameof(ShowDownloadPage), nameof(ShowWhatsNew), nameof(ShowCancel), nameof(ShowRetry), nameof(ShowProgress), nameof(ShowClose), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private UpdatePhase _phase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateNow), nameof(ShowDownloadPage), nameof(ShowWhatsNew), nameof(ShowRetry))]
    private UpdateRelease? _release;

    [ObservableProperty]
    private string _message = string.Empty;

    /// <summary>0–100 while downloading; -1 when the size is not known yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressUnknown))]
    private double _progress;

    [ObservableProperty]
    private bool _isBarVisible;

    public UpdateService(AppPaths paths, DownloadService downloads, IDialogService dialogs, TrayService tray, UpdateChecker checker, UpdateDownloader downloader, ILogger<UpdateService> logger)
    {
        _paths = paths;
        _downloads = downloads;
        _dialogs = dialogs;
        _tray = tray;
        _checker = checker;
        _downloader = downloader;
        _logger = logger;
        Current = ReleaseVersion.OfApp(AppInfo.Version);
        CannotInstallReason = WhyNotInstallable();
        _hideTimer = new DispatcherTimer(UpToDateShownFor, DispatcherPriority.Background, (_, _) =>
        {
            _hideTimer!.Stop();
            if (Phase == UpdatePhase.UpToDate)
            {
                IsBarVisible = false;
            }
        }, Dispatcher.CurrentDispatcher);
        _hideTimer.Stop();
    }

    public Version Current { get; }

    /// <summary>Why this copy can't replace its own files (then only the release page is offered); null if it can.</summary>
    public string? CannotInstallReason { get; }

    /// <summary>Set by the app: is the main window on screen (else the new version starts in the tray too).</summary>
    public Func<bool>? IsWindowVisible { get; set; }

    /// <summary>Set by the app: exit for real, as the tray's Exit does.</summary>
    public Action? RequestExit { get; set; }

    /// <summary>The <c>--data</c> folder this copy was started with, passed on to the new version.</summary>
    public string? DataRootArgument { get; set; }

    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;

    private bool CanInstallRelease => Release is { CanInstall: true } && CannotInstallReason is null && _downloader.HasTrustedKey;

    public bool ShowUpdateNow => Phase == UpdatePhase.Available && CanInstallRelease;

    public bool ShowDownloadPage => Release is not null && ((Phase == UpdatePhase.Available && !CanInstallRelease) || Phase == UpdatePhase.Failed);

    public bool ShowWhatsNew => Release is not null && Phase is UpdatePhase.Available or UpdatePhase.Downloading;

    public bool ShowCancel => Phase == UpdatePhase.Downloading;

    public bool ShowRetry => Phase == UpdatePhase.Failed && CanInstallRelease;

    public bool ShowProgress => Phase == UpdatePhase.Downloading;

    public bool IsProgressUnknown => Progress < 0;

    public bool ShowClose => !IsBusy;

    private static string InstallFolder => AppContext.BaseDirectory;

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(InstallFolder, UpdateInstaller.AppExe);

    private string RecordPath => Path.Combine(_paths.UpdatesDirectory, "installed.json");

    /// <summary>After startup: finishes an update this start is the result of. Nothing is checked until the user asks.</summary>
    public void Start() => FinishInstall();

    public void Dispose()
    {
        _hideTimer.Stop();
        _cancel?.Cancel();
        _cancel?.Dispose();
        // The downloader belongs to the container, which disposes it.
    }

    /// <summary>The toolbar's "Updates": the only time OB2 asks GitHub. Always says what it found.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync()
    {
        if (IsBusy)
        {
            return;
        }

        Phase = UpdatePhase.Checking;
        Message = Loc.Get("Update_Checking");
        IsBarVisible = true;

        UpdateRelease? release;
        try
        {
            release = await _checker.CheckAsync(Current);
        }
        catch (UpdateException ex)
        {
            _logger.LogInformation("Update check failed: {Reason}", ex.Message);
            Release = null;
            Fail(ex.Message);
            return;
        }

        _logger.LogInformation("Update check: {Result}", release is null ? "up to date" : $"{ReleaseVersion.Text(release.Version)} available");
        Release = release;
        if (release is null)
        {
            Phase = UpdatePhase.UpToDate;
            Message = Loc.Format("Update_UpToDate", ReleaseVersion.Text(Current));
            _hideTimer.Start();
            return;
        }

        Phase = UpdatePhase.Available;
        Message = AvailableText(release);
    }

    private bool CanCheckNow() => !IsBusy;

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (Release is not { } release || !CanInstallRelease)
        {
            return;
        }

        var running = _downloads.List().Where(d => _downloads.Engine.IsActive(d.Id)).Select(d => d.Id).ToList();
        if (running.Count > 0 && !_dialogs.ConfirmUpdate(running.Count))
        {
            return;
        }

        _cancel = new CancellationTokenSource();
        Phase = UpdatePhase.Downloading;
        Progress = -1;
        var version = ReleaseVersion.Text(release.Version);
        Message = Loc.Format("Update_Downloading", version, string.Empty);
        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            Progress = p.Total is > 0 ? Math.Min(100, p.Received * 100.0 / p.Total.Value) : -1;
            Message = Loc.Format("Update_Downloading", version, p.Total is > 0 ? $"{Fmt.Size(p.Received)} / {Fmt.Size(p.Total)}" : Fmt.Size(p.Received));
        });

        try
        {
            var prepared = await _downloader.DownloadAsync(release, progress, _cancel.Token);
            Phase = UpdatePhase.Installing;
            Message = Loc.Format("Update_Installing", version);
            await InstallAndRestartAsync(prepared);
        }
        catch (OperationCanceledException)
        {
            // Kept for next time: the download continues where it stopped.
            Phase = UpdatePhase.Available;
            Message = AvailableText(release);
        }
        catch (UpdateException ex)
        {
            _logger.LogWarning(ex, "Update to {Version} failed", version);
            Fail(ex.Message);
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancel?.Cancel();

    [RelayCommand]
    private void Close()
    {
        if (!IsBusy)
        {
            IsBarVisible = false;
        }
    }

    [RelayCommand]
    private void OpenPage()
    {
        if (Release is { } release)
        {
            _dialogs.OpenUrl(release.Page);
        }
    }

    private string AvailableText(UpdateRelease release)
    {
        var text = Loc.Format("Update_Available", ReleaseVersion.Text(release.Version), ReleaseVersion.Text(Current));
        if (!CanInstallRelease)
        {
            text += " " + (CannotInstallReason ?? Loc.Get(_downloader.HasTrustedKey ? "Update_NoFiles" : "Update_NoKey"));
        }

        return text;
    }

    private void Fail(string message)
    {
        Phase = UpdatePhase.Failed;
        Message = message;
        IsBarVisible = true;
    }

    /// <summary>Pauses what runs, swaps the files, starts the new version and exits; on failure puts everything back.</summary>
    private async Task InstallAndRestartAsync(PreparedUpdate prepared)
    {
        var running = _downloads.List().Where(d => _downloads.Engine.IsActive(d.Id)).Select(d => d.Id).ToList();
        await _downloads.StopAsync(running); // checkpointed: they continue in the new version

        InstallRecord record;
        try
        {
            record = await Task.Run(() => UpdateInstaller.Install(prepared.Folder, InstallFolder, Current, prepared.Version));
        }
        catch (UpdateException)
        {
            _downloads.Start(running);
            throw;
        }

        record.Resume = running;
        try
        {
            await record.SaveAsync(RecordPath);
            var start = new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = InstallFolder };
            start.ArgumentList.Add(StartupArguments.AfterUpdateOption);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (IsWindowVisible?.Invoke() != true)
            {
                start.ArgumentList.Add("--minimized");
            }

            if (DataRootArgument is { } data)
            {
                start.ArgumentList.Add("--data");
                start.ArgumentList.Add(data);
            }

            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            UpdateInstaller.Undo(InstallFolder, record);
            TryDelete(RecordPath);
            _downloads.Start(running);
            throw new UpdateException(Loc.Format("Update_RestartFailed", ex.Message), ex);
        }

        _logger.LogInformation("Update: {From} replaced by {To}; restarting", record.From, record.To);
        RequestExit?.Invoke();
    }

    /// <summary>In the version an update just started: clean up, continue the paused downloads, say so.</summary>
    private void FinishInstall()
    {
        if (InstallRecord.Load(RecordPath) is not { } record)
        {
            return;
        }

        var left = UpdateInstaller.RemoveLeftovers(InstallFolder, record);
        if (left.Count > 0)
        {
            _logger.LogWarning("Update: could not remove {Files} yet", string.Join(", ", left));
        }

        TryDelete(RecordPath);
        TryDeleteFolder(Path.Combine(_paths.UpdatesDirectory, record.To));
        if (record.To != ReleaseVersion.Text(Current))
        {
            _logger.LogInformation("Update to {To} was undone; running {Current}", record.To, ReleaseVersion.Text(Current));
            return; // went back to the previous version
        }

        _logger.LogInformation("Update: now running {To} (was {From})", record.To, record.From);
        var resume = record.Resume.Where(id => _downloads.Get(id) is { Status: DownloadStatus.Paused }).ToList();
        _downloads.Start(resume);

        Phase = UpdatePhase.Updated;
        Message = Loc.Format("Update_Updated", record.To) + (record.ExtensionChanged ? " " + Loc.Get("Update_ReloadExtension") : string.Empty);
        IsBarVisible = true;
        if (IsWindowVisible?.Invoke() != true)
        {
            _tray.Notify(Loc.Format("Update_Updated", record.To), record.ExtensionChanged ? Loc.Get("Update_ReloadExtension") : string.Empty, isError: false);
        }
    }

    private string? WhyNotInstallable()
    {
        if (!string.Equals(Path.GetFileName(ExePath), UpdateInstaller.AppExe, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.Get("Update_NotOb2Exe");
        }

        // A build from source runs as OB2.dll beside a small OB2.exe; git updates it, not this.
        if (File.Exists(Path.Combine(InstallFolder, "OB2.dll")))
        {
            return Loc.Get("Update_SourceBuild");
        }

        return _paths.FellBackFromReadOnly || !IsWritable(InstallFolder) ? Loc.Get("Update_ReadOnly") : null;
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".ob2-update-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Harmless: an outdated record is ignored next time.
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed with the next update's unpacking.
        }
    }
}
