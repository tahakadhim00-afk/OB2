using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OB2.App.Services;
using OB2.App.ViewModels;
using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Organization;
using OB2.Core.Services;
using OB2.Core.Time;
using OB2.Storage;
using OB2.Storage.Repositories;

namespace OB2.App;

/// <summary>The only place where services are wired together.</summary>
internal static class CompositionRoot
{
    public static ServiceProvider Build(StorageContext storage)
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

        services.AddSingleton(sp => new DownloadEngine(
            sp.GetRequiredService<IDownloadRepository>(),
            () => EngineOptions.FromSettings(storage.Settings),
            clock: sp.GetRequiredService<IClock>(),
            loggerFactory: storage.LoggerFactory));
        services.AddSingleton<QueueManager>();
        services.AddSingleton(sp => new QueueScheduler(sp.GetRequiredService<QueueManager>(), sp.GetRequiredService<IQueueRepository>(), sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new DownloadService(
            sp.GetRequiredService<DownloadEngine>(),
            sp.GetRequiredService<IDownloadRepository>(),
            sp.GetRequiredService<ICategoryRepository>(),
            sp.GetRequiredService<IQueueRepository>(),
            sp.GetRequiredService<QueueManager>(),
            () => storage.Settings));

        services.AddSingleton<IDialogService, DialogService>();
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
