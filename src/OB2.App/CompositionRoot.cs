using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OB2.App.Services;
using OB2.App.ViewModels;
using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Media;
using OB2.Core.Organization;
using OB2.Core.Services;
using OB2.Core.Time;
using OB2.Shared.Ipc;
using OB2.Storage;
using OB2.Storage.Paths;
using OB2.Storage.Repositories;
using System.IO;

namespace OB2.App;

/// <summary>The only place where services are wired together.</summary>
internal static class CompositionRoot
{
    /// <param name="explicitDataRoot">The <c>--data</c> folder, if given, so "Start with Windows" starts the same data.</param>
    public static ServiceProvider Build(StorageContext storage, string? explicitDataRoot = null)
    {
        var services = new ServiceCollection();

        // Storage owns the logger factory's lifetime, so register instances, not factories.
        services.AddSingleton(storage);
        services.AddSingleton(storage.Paths);
        services.AddSingleton(storage.Settings);
        services.AddSingleton(storage.SettingsStore);
        services.AddSingleton(storage.ConnectionFactory);
        services.AddSingleton(storage.LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IClock>(SystemClock.Instance);

        services.AddSingleton<IDownloadRepository>(sp => new SqliteDownloadRepository(storage.ConnectionFactory, sp.GetRequiredService<IClock>()));
        services.AddSingleton(_ => new SqliteOrganizationRepository(storage.ConnectionFactory));
        services.AddSingleton<ICategoryRepository>(sp => sp.GetRequiredService<SqliteOrganizationRepository>());
        services.AddSingleton<IQueueRepository>(sp => sp.GetRequiredService<SqliteOrganizationRepository>());

        // D-71: yt-dlp, Deno and ffmpeg for video pages, downloaded into the data root on first use.
        services.AddSingleton(_ => new MediaTools(storage.Paths.ToolsDirectory));
        services.AddSingleton(sp => new MediaInspector(sp.GetRequiredService<MediaTools>(), sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new MediaToolInstaller(sp.GetRequiredService<MediaTools>(), storage.LoggerFactory));

        services.AddSingleton(sp => new DownloadEngine(
            sp.GetRequiredService<IDownloadRepository>(),
            () => EngineOptions.FromSettings(storage.Settings),
            clock: sp.GetRequiredService<IClock>(),
            loggerFactory: storage.LoggerFactory,
            cookieStore: new DpapiCookieStore(storage.ConnectionFactory, sp.GetRequiredService<IClock>()),
            mediaTools: sp.GetRequiredService<MediaTools>()));
        services.AddSingleton<QueueManager>();
        services.AddSingleton(sp => new QueueScheduler(sp.GetRequiredService<QueueManager>(), sp.GetRequiredService<IQueueRepository>(), sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new BandwidthSchedule(
            sp.GetRequiredService<DownloadEngine>().GlobalLimiter,
            () => storage.Settings.SpeedLimits,
            sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new DownloadService(
            sp.GetRequiredService<DownloadEngine>(),
            sp.GetRequiredService<IDownloadRepository>(),
            sp.GetRequiredService<ICategoryRepository>(),
            sp.GetRequiredService<IQueueRepository>(),
            sp.GetRequiredService<QueueManager>(),
            () => storage.Settings));

        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<BrowserBridge>();
        services.AddSingleton(_ => new NativeHostRegistration(
            AppContext.BaseDirectory,
            // The manifest lives next to host.exe unless that folder is read-only (§8.2, §12.3).
            storage.Paths.FellBackFromReadOnly ? storage.Paths.DataDirectory : AppContext.BaseDirectory));
        services.AddSingleton(_ => new StartupRegistration(
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OB2.exe"),
            explicitDataRoot));
        services.AddSingleton<TrayService>();
        services.AddSingleton<CompletionActionService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
