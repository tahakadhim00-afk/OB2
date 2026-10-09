using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OB2.Core.Time;
using OB2.Storage;
using OB2.App.ViewModels;

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

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
