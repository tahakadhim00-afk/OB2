using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OB2.Storage;
using OB2.Storage.Paths;

namespace OB2.App;

public partial class App : Application
{
    private StorageContext? _storage;
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _storage = await StorageBootstrapper.InitializeAsync(AppPathEnvironment.Current);
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

        _services = CompositionRoot.Build(_storage);
        _logger = _services.GetRequiredService<ILogger<App>>();
        RegisterErrorBoundary();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("OB2 exiting with code {ExitCode}", e.ApplicationExitCode);
        _services?.Dispose();
        _storage?.Dispose();
        base.OnExit(e);
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
