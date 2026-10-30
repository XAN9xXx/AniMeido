using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Base.Services;

public static class MetadataServiceExtensions
{
    public static IServiceCollection AddMetadataService(this IServiceCollection services)
    {
        services.AddHttpClient(MetadataServiceSettings.ClientName, MetadataServiceSettings.ConfigureClient);
        services.AddSingleton<PublicServiceRequests>(sp => new PublicServiceRequests(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(MetadataServiceSettings.ClientName),
            logger: sp.GetRequiredService<ILogger<PublicServiceRequests>>()));
        services.AddSingleton<MetadataDataSource>(sp => new MetadataDataSource(
            sp.GetRequiredService<ILogger<MetadataDataSource>>(),
            sp.GetRequiredService<PublicServiceRequests>(),
            sp.GetRequiredService<CacheService>()));
        services.AddSingleton<IAnimeDataSource, PersonalAnimeDataSource>();
        services.AddSingleton<PersonalIdRemapService>();
        services.AddSingleton<PendingReviewService>();
        services.AddSingleton<PublicIdCheckService>(sp => new PublicIdCheckService(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<PublicServiceRequests>(),
            sp.GetRequiredService<MetadataDataSource>(), sp.GetRequiredService<PersonalIdRemapService>(),
            sp.GetRequiredService<ILogger<PublicIdCheckService>>()));
        services.AddSingleton<PublicIdMigrationService>(sp => new PublicIdMigrationService(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<PublicServiceRequests>(),
            sp.GetRequiredService<ILogger<PublicIdMigrationService>>()));
        services.AddSingleton<TrackingService>();
        services.AddSingleton<PersonalFeatureStateStore>();
        services.AddSingleton<CacheService>(sp => new CacheService(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            ImageCacheHelper.ClearAll,
            () => ImageCacheHelper.GetCacheStats().sizeMB * 1024,
            sp.GetRequiredService<ILogger<CacheService>>()));
        services.AddSingleton<BrowseHistoryService>();
        services.AddSingleton<BackupService>();
        return services;
    }
}
