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
        _schedulerTimer?.Stop();
        _trayTimer?.Stop();
        _memoryTimer?.Stop();
        _pipeServer?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));

        // Disposing the engine checkpoints and pauses every running download (§11.3).
        _services?.Dispose();
        _storage?.Dispose();
        base.OnExit(e);
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
        engine.GlobalLimiter.BytesPerSecond = settings.SpeedLimits.GlobalBytesPerSecond;

        // §11.3: interrupted downloads come back paused unless the user opted into auto-resume.
        // The window's view model subscribes first so it sees every status change.
        IReadOnlyList<long> interrupted = _storage!.InstanceLock.IsFirstInstance ? engine.RecoverInterrupted() : [];
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

        // FR-16: evaluate schedules now and every 15 seconds.
        var scheduler = services.GetRequiredService<QueueScheduler>();
        _schedulerTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, async (_, _) => await TickSchedulerAsync(scheduler), Dispatcher);
        _ = TickSchedulerAsync(scheduler);

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

    private async Task TickSchedulerAsync(QueueScheduler scheduler)
    {
        try
        {
            await scheduler.TickAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogError(ex, "Scheduler tick failed");
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
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            Format("Error_UnexpectedMessage", e.Exception.Message),
            Text("Error_UnexpectedTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private string Text(string key) => (string)FindResource(key);

    private string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), args);
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
