using AniSync.Next.Application;
using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using AniSync.Next.Host;
using AniSync.Next.Persistence;
using AniSync.Next.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;

namespace AniSync.Next;

public sealed class PluginServiceRegistration : IPluginServiceRegistration
{
    public static void RegisterServices(IServiceCollection services, IApplicationPaths applicationPaths)
    {
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddHttpClient(HttpClientNames.Mapping, client =>
        {
            client.BaseAddress = new Uri("https://arm.haglund.dev/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient(HttpClientNames.MyAnimeList,
            client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddHttpClient(HttpClientNames.AniList,
            client => client.Timeout = TimeSpan.FromSeconds(20));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPluginConfigurationService, PluginConfigurationService>();
        services.AddSingleton<IAniSyncDiagnostics, AniSyncDiagnostics>();
        services.AddSingleton<IPluginStateStore>(provider => new JsonPluginStateStore(
            Path.Combine(applicationPaths.PluginsPath, "AniSyncNext"),
            provider.GetRequiredService<ILogger<JsonPluginStateStore>>()));
        services.AddSingleton<IReviewStore>(provider => provider.GetRequiredService<IPluginStateStore>());
        services.AddSingleton<IHistoryStore>(provider => provider.GetRequiredService<IPluginStateStore>());
        services.AddSingleton<IMappingResolver, MappingResolver>();
        services.AddSingleton<IShokoStateReader, ShokoStateReader>();
        services.AddSingleton<ISyncPlanner, SyncPlanner>();
        services.AddSingleton<ISyncExecutor, SyncExecutor>();
        services.AddSingleton<ISyncCoordinator, SyncCoordinator>();
        services.AddSingleton<IProviderDelay, ProviderDelay>();
        services.AddSingleton<IProviderTokenService, ProviderTokenService>();
        services.AddSingleton<IOAuthStateService, OAuthStateService>();
        services.AddSingleton<IProviderOAuthService, ProviderOAuthService>();
        services.AddSingleton<ProviderHttpTransport>();
        services.AddSingleton<ISyncProvider, MyAnimeListProvider>();
        services.AddSingleton<ISyncProvider, AniListProvider>();
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton<ISyncTriggerQueue, SyncTriggerQueue>();

        // Shoko stops hosted services in reverse registration order. Register the
        // worker first so the event bridge unsubscribes before the queue drains.
        services.AddSingleton<SyncWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<SyncWorker>());
        services.AddSingleton<ShokoEventBridge>();
        services.AddHostedService(provider => provider.GetRequiredService<ShokoEventBridge>());
    }
}

public sealed class PluginApplicationRegistration : IPluginApplicationRegistration
{
    public static void RegisterServices(IApplicationBuilder application, IApplicationPaths applicationPaths) { }
}
