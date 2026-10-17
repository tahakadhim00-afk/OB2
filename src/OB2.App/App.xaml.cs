using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OB2.App.Infrastructure;
using OB2.App.Services;
using OB2.App.ViewModels;
using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Organization;
using OB2.Core.Services;
using OB2.Core.Settings;
using OB2.Shared.Ipc;
using OB2.Shared.Protocol;
using OB2.Storage;
using OB2.Storage.Paths;
using System.IO;

namespace OB2.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WPF owns the Application lifetime; every disposable field is released in OnExit.")]
public partial class App : Application
{
    private StorageContext? _storage;
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private DispatcherTimer? _schedulerTimer;
    private DispatcherTimer? _trayTimer;
    private DispatcherTimer? _memoryTimer;
    private PipeServer? _pipeServer;
    private MainWindow? _window;
    private bool _exiting;

    /// <summary>Parsed command line: <c>[url] [--data dir] [--minimized]</c>.</summary>
    internal StartupArguments Arguments { get; private set; } = StartupArguments.Empty;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Arguments = StartupArguments.Parse(e.Args);

        try
        {
            var paths = Arguments.DataRoot is { } root ? AppPaths.ForRoot(root) : AppPaths.Resolve(AppPathEnvironment.Current);
            _storage = await StorageBootstrapper.InitializeAsync(paths);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Format("Startup_FailedMessage", ex.Message),
                Text("Startup_FailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Composing the services and opening the window can fail too (a locked database, a
        // tray icon Explorer will not accept, a corrupt settings file). Until the error
        // boundary is registered nothing would catch it, so the whole sequence reports the
        // same way the storage failure above does rather than dying without a word.
        try
        {
            var settings = _storage.Settings;
            if (string.IsNullOrEmpty(settings.Ipc.PipeName))
            {
                // §11.1: per-install random pipe name, read by host.exe from settings.json.
                settings.Ipc.PipeName = IpcSettings.NewPipeName();
                await _storage.SettingsStore.SaveAsync(settings);
            }

            if (!_storage.InstanceLock.IsFirstInstance && await ForwardToRunningInstanceAsync(settings.Ipc.PipeName))
            {
                // §8.5: a second ob2.exe hands its URL (or just "show yourself") to the running one.
                _storage.Dispose();
                _storage = null;
                Shutdown(0);
                return;
            }

            _services = CompositionRoot.Build(_storage, Arguments.DataRoot);
            _logger = _services.GetRequiredService<ILogger<App>>();
            RegisterErrorBoundary();
            StartServices();
            StartBrowserBridge(settings.Ipc.PipeName);
            SyncStartWithWindows(settings);
        }
        catch (Exception ex)
        {
            // _logger may not exist yet, so the dialog is the one guaranteed report.
            _logger?.LogCritical(ex, "Startup failed");
            MessageBox.Show(
                Format("Startup_FailedMessage", ex.Message),
                Text("Startup_FailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task<bool> ForwardToRunningInstanceAsync(string pipeName)
    {
        var request = Arguments.Url is { } url
            ? ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload { Url = url, Source = "cli" })
            : ProtocolMessage.Simple(MessageTypes.Activate);
        try
        {
            var reply = await PipeClient.SendAsync(pipeName, request, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
            return reply.Type != MessageTypes.Error;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ProtocolException)
        {
            // The other process holds the folder but is not answering (still starting, or a CLI run): open normally.
            return false;
        }
    }

    private void StartBrowserBridge(string pipeName)
    {
        var services = _services!;
        var bridge = services.GetRequiredService<BrowserBridge>();
        bridge.ActivateWindow = () => _window?.ShowAndActivate();
        _pipeServer = new PipeServer(pipeName, bridge, ex => _logger?.LogError(ex, "Pipe request failed"));
        _pipeServer.Start();

        // §12.6: the folder moved since registration; point Chrome at this copy.
        var registration = services.GetRequiredService<NativeHostRegistration>();
        try
        {
            if (registration.HostExists && registration.NeedsRepair())
            {
                registration.Register();
                _logger?.LogInformation("Browser integration re-registered for {Path}", registration.HostPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger?.LogWarning(ex, "Browser integration could not be re-registered");
        }
    }

    /// <summary>FR-31: keep the Run value in step with the setting, e.g. after the folder moved.</summary>
    private void SyncStartWithWindows(AppSettings settings)
    {
        // The setting is on by default, but a portable copy never registers itself unprompted:
        // its path may be a removable drive, which would leave the sign-in pointing at nothing.
        // It touches nothing here at all — not even to remove a value — because the value it
        // found most likely belongs to an installed copy on this machine. Ticking the option in
        // Settings still works for a portable copy; that path passes takeOver.
        if (_storage!.Paths.IsPortable)
        {
            return;
        }

        try
        {
            if (_services!.GetRequiredService<StartupRegistration>().Sync(settings.General.StartWithWindows, takeOver: false))
            {
                _logger?.LogInformation("Start with Windows {State}", settings.General.StartWithWindows ? "registered" : "removed");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger?.LogWarning(ex, "Start with Windows could not be updated");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("OB2 exiting with code {ExitCode}", e.ApplicationExitCode);

        // Every step runs even if an earlier one fails. The instance lock is a FileShare.None
        // file handle released by _storage, so skipping it would leave the next launch thinking
        // another copy is still running (§8.5).
        CloseQuietly("timers", () =>
        {
            _schedulerTimer?.Stop();
            _trayTimer?.Stop();
            _memoryTimer?.Stop();
        });

        CloseQuietly("pipe server", () => _pipeServer?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)));

        // Disposing the engine checkpoints and pauses every running download (§11.3).
        CloseQuietly("services", () => _services?.Dispose());
        CloseQuietly("storage", () => _storage?.Dispose());

        base.OnExit(e);
    }

    /// <summary>
    /// §10.4: applies the history retention limit once per start. Null keeps history forever,
    /// which is the default. A failure here must not stop the app from opening.
    /// </summary>
    private void PruneHistory(AppSettings settings)
    {
        if (settings.History.RetentionDays is not { } days || days <= 0)
        {
            return;
        }

        try
        {
            var removed = _services!.GetRequiredService<IDownloadRepository>().PruneHistory(days);
            if (removed > 0)
            {
                _logger?.LogInformation("History: {Count} events older than {Days} days removed", removed, days);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "History could not be pruned");
        }
    }

    /// <summary>Runs one shutdown step, logging rather than propagating a failure.</summary>
    private void CloseQuietly(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Shutdown step {Step} failed", step);
        }
    }

    /// <summary>Exit from the tray or a completion action: really close instead of hiding.</summary>
    internal void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        if (_window is not null)
        {
            _window.IsExiting = true;
            _window.Close();
        }

        Shutdown();
    }

    private void StartServices()
    {
        var services = _services!;
        var settings = services.GetRequiredService<AppSettings>();
        var downloads = services.GetRequiredService<DownloadService>();
        RunFirstRunWizard(settings, downloads);
        var engine = services.GetRequiredService<DownloadEngine>();
        // FR-9: sets the limiter from the plain limit or the window's, whichever applies now.
        services.GetRequiredService<BandwidthSchedule>().Apply();

        // §11.3: interrupted downloads come back paused unless the user opted into auto-resume.
        // The window's view model subscribes first so it sees every status change.
        IReadOnlyList<long> interrupted = _storage!.InstanceLock.IsFirstInstance ? engine.RecoverInterrupted() : [];
        if (_storage.InstanceLock.IsFirstInstance)
        {
            PruneHistory(settings);
        }

        var viewModel = services.GetRequiredService<MainViewModel>();
        if (settings.Downloads.AutoResumeOnStartup)
        {
            downloads.Start(interrupted);
        }

        _window = services.GetRequiredService<MainWindow>();
        _window.ShouldHideOnClose = () => settings.General.MinimizeToTrayOnClose;
        _window.Closed += (_, _) => ExitApplication();
        MainWindow = _window;

        var tray = services.GetRequiredService<TrayService>();
        tray.Initialize();
        tray.ToggleRequested += (_, _) => ToggleWindow();
        tray.ShowRequested += (_, _) => _window.ShowAndActivate();
        tray.PauseAllRequested += async (_, _) => await downloads.StopAllAsync();
        tray.ResumeAllRequested += (_, _) => downloads.StartAll();
        tray.OptionsRequested += (_, _) => viewModel.OptionsCommand.Execute(null);
        tray.ExitRequested += (_, _) => ExitApplication();

        var completion = services.GetRequiredService<CompletionActionService>();
        completion.ExitApp = ExitApplication;

        downloads.Completed += (_, item) => Dispatcher.BeginInvoke(() => OnDownloadCompleted(item));
        downloads.Failed += (_, item) => Dispatcher.BeginInvoke(() => OnDownloadFailed(item));
        downloads.Queues.QueueCompleted += (_, args) => Dispatcher.BeginInvoke(() =>
        {
            if (settings.General.ShowNotifications)
            {
                tray.Notify(Text("App_Title"), Format("Notify_QueueDone", args.Queue.Name), isError: false);
            }

            completion.Run(args.Queue);
        });

        // FR-16: evaluate schedules now and every 15 seconds. The bandwidth window (FR-9) rides
        // the same tick, so the rate changes within 15 seconds of the window opening or closing.
        var scheduler = services.GetRequiredService<QueueScheduler>();
        var bandwidth = services.GetRequiredService<BandwidthSchedule>();
        _schedulerTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, async (_, _) => await TickSchedulerAsync(scheduler, bandwidth), Dispatcher);
        _ = TickSchedulerAsync(scheduler, bandwidth);

        _trayTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            var (active, speed) = viewModel.Totals;
            tray.SetToolTip(Format("Tray_Tooltip", active, Fmt.Speed(speed) is { Length: > 0 } s ? s : "0 B/s"));
        }, Dispatcher);

        // NFR / AC-12: a memory line in the log every 5 minutes makes leaks visible in the field.
        // OB2_MEMORY_LOG_SECONDS shortens the interval for diagnosis.
        var memoryInterval = int.TryParse(Environment.GetEnvironmentVariable("OB2_MEMORY_LOG_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(5);
        _memoryTimer = new DispatcherTimer(memoryInterval, DispatcherPriority.Background, (_, _) =>
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            _logger?.LogInformation(
                "Memory: managed heap {HeapMb:0.0} MB, GC committed {CommittedMb:0.0} MB, private {PrivateMb:0.0} MB, working set {WorkingSetMb:0.0} MB, GC gen0/1/2 {G0}/{G1}/{G2}, active downloads {Active}",
                GC.GetTotalMemory(false) / 1048576.0,
                gc.TotalCommittedBytes / 1048576.0,
                process.PrivateMemorySize64 / 1048576.0,
                process.WorkingSet64 / 1048576.0,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                viewModel.Totals.Active);
        }, Dispatcher);

        if (!Arguments.Minimized)
        {
            _window.ContentRendered += (_, _) => LogStartupTime("window shown");
            _window.Show();
        }
        else
        {
            LogStartupTime("tray ready");
        }

        if (Arguments.Url is { } url)
        {
            _window.ShowAndActivate();
            viewModel.AddFromPrefill(new AddDownloadPrefill { Url = url, Source = DownloadSource.Cli });
        }
    }

    /// <summary>
    /// Flow D step 2: on first launch, ask where downloads go. Skipped for tray-only starts
    /// (browser capture), which use the Downloads folder until the window is opened.
    /// </summary>
    private void RunFirstRunWizard(AppSettings settings, DownloadService downloads)
    {
        if (settings.Folders.DefaultSaveFolder is not null || Arguments.Minimized)
        {
            return;
        }

        var paths = _storage!.Paths;
        var wizard = new Views.FirstRunWindow(downloads.DefaultSaveFolder, paths.IsPortable, paths.Root);
        if (wizard.ShowDialog() == true && wizard.ChosenFolder is { } folder)
        {
            settings.Folders.DefaultSaveFolder = folder;
            _storage.SettingsStore.SaveAsync(settings).GetAwaiter().GetResult();
            _logger?.LogInformation("First run: save folder set");
        }
    }

    /// <summary>§16.1: single-file startup cost is measured on every launch.</summary>
    private void LogStartupTime(string stage)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        _logger?.LogInformation("Startup: {Stage} {Ms:0} ms after process start", stage, (DateTime.Now - process.StartTime).TotalMilliseconds);
    }

    private async Task TickSchedulerAsync(QueueScheduler scheduler, BandwidthSchedule bandwidth)
    {
        // Independent of each other: a failing queue schedule must not leave the speed limit
        // stuck at whatever it was when the window last changed, or the reverse.
        try
        {
            await scheduler.TickAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogError(ex, "Scheduler tick failed");
        }

        try
        {
            var wasOpen = bandwidth.IsWindowOpen;
            bandwidth.Apply();
            if (bandwidth.IsWindowOpen != wasOpen)
            {
                _logger?.LogInformation(
                    "Bandwidth window {State}; limit now {Limit}",
                    bandwidth.IsWindowOpen ? "open" : "closed",
                    bandwidth.EffectiveBytesPerSecond?.ToString(CultureInfo.InvariantCulture) ?? "unlimited");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogError(ex, "Bandwidth window tick failed");
        }
    }

    private void ToggleWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible && _window.WindowState != WindowState.Minimized)
        {
            _window.Hide();
        }
        else
        {
            _window.ShowAndActivate();
        }
    }

    private void OnDownloadCompleted(DownloadItem item)
    {
        var services = _services!;
        var settings = services.GetRequiredService<AppSettings>();
        var downloads = services.GetRequiredService<DownloadService>();
        if (settings.General.ShowNotifications)
        {
            services.GetRequiredService<TrayService>().Notify(Text("Notify_Completed"), item.FileName, isError: false);
        }

        // Queue runs can finish many files; only downloads started outside a running queue get the dialog.
        var category = downloads.Categories.FirstOrDefault(c => c.Id == item.CategoryId)?.Name ?? string.Empty;
        var inRunningQueue = item.QueueId is { } q && downloads.Queues.IsRunning(q);
        if (settings.General.ShowCompleteDialog && !inRunningQueue && !settings.General.CompleteDialogHiddenCategories.Contains(category))
        {
            services.GetRequiredService<IDialogService>().ShowComplete(item);
        }
    }

    private void OnDownloadFailed(DownloadItem item)
    {
        var services = _services!;
        if (services.GetRequiredService<AppSettings>().General.ShowNotifications)
        {
            services.GetRequiredService<TrayService>().Notify(Text("Notify_Failed"), $"{item.FileName}: {item.ErrorMessage}", isError: true);
        }
    }

    /// <summary>
    /// Global error boundary (§11.3): UI-thread exceptions are logged and shown in a dialog
    /// instead of crashing; background exceptions are logged.
    /// </summary>
    private void RegisterErrorBoundary()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {IsTerminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // The boundary itself must not throw: a second exception raised in here would not be
        // handled again and would take the process down, which is the very thing it prevents.
        // Marking it handled first keeps the app alive even if reporting fails.
        e.Handled = true;

        try
        {
            _logger?.LogError(e.Exception, "Unhandled UI exception");
        }
        catch (Exception logFailure)
        {
            System.Diagnostics.Debug.WriteLine($"Error boundary could not log: {logFailure}");
        }

        try
        {
            MessageBox.Show(
                Format("Error_UnexpectedMessage", e.Exception.Message),
                Text("Error_UnexpectedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception dialogFailure)
        {
            System.Diagnostics.Debug.WriteLine($"Error boundary could not report: {dialogFailure}");
        }
    }

    /// <summary>
    /// Resource lookup that cannot throw. <see cref="FrameworkElement.FindResource"/> raises on a
    /// missing key, which is not survivable inside the error boundary, so a missing string falls
    /// back to its key the way <see cref="Loc.Get"/> does everywhere else.
    /// </summary>
    private string Text(string key) => TryFindResource(key) as string ?? key;

    private string Format(string key, params object[] args)
    {
        var format = Text(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            // A malformed or placeholder-free string must not mask the error being reported.
            return args.Length > 0 ? $"{format} {string.Join(' ', args)}" : format;
        }
    }
}

/// <summary>GUI command line (§8.5): <c>ob2.exe ["https://…"] [--data dir] [--minimized] [--from-host]</c>.</summary>
internal sealed record StartupArguments(string? Url, string? DataRoot, bool Minimized, bool FromHost)
{
    public static StartupArguments Empty { get; } = new(null, null, false, false);

    public static StartupArguments Parse(IReadOnlyList<string> args)
    {
        string? url = null, data = null;
        bool minimized = false, fromHost = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--data" when i + 1 < args.Count:
                    data = args[++i];
                    break;
                case "--minimized":
                    minimized = true;
                    break;
                case "--from-host":
                    fromHost = true;
                    minimized = true;
                    break;
                default:
                    if (Uri.TryCreate(args[i], UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        url = args[i];
                    }

                    break;
            }
        }

        return new StartupArguments(url, data, minimized, fromHost);
    }
}
