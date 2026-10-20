using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Base.Services;

internal static class MetadataServiceSettings
{
    internal const string QueryBaseUrl = "https://api.animeido.com:38443";
    internal const string ImageBaseUrl = "https://img.animeido.com:38443";
    internal const string ClientName = "AniMeidoMetadata";

    internal static void ConfigureClient(HttpClient client)
    {
        client.BaseAddress = new Uri(QueryBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(10);
        var version = typeof(MetadataDataSource).Assembly.GetName().Version?.ToString(3);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", $"XAN9xXx/AniMeido/{version} (https://github.com/XAN9xXx/AniMeido)");
    }
}

public static class MetadataServiceExtensions
{
    public static IServiceCollection AddMetadataService(this IServiceCollection services)
    {
        services.AddHttpClient(MetadataServiceSettings.ClientName, MetadataServiceSettings.ConfigureClient);
        services.AddSingleton<MetadataDataSource>(sp => new MetadataDataSource(
            sp.GetRequiredService<ILogger<MetadataDataSource>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(MetadataServiceSettings.ClientName),
            sp.GetRequiredService<CacheService>()));
        services.AddSingleton<IAnimeDataSource, PersonalAnimeDataSource>();
        services.AddSingleton<PublicIdMigrationService>(sp => new PublicIdMigrationService(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(MetadataServiceSettings.ClientName),
            sp.GetRequiredService<ILogger<PublicIdMigrationService>>()));
        services.AddSingleton<TrackingService>();
        services.AddSingleton<CacheService>();
        services.AddSingleton<BrowseHistoryService>();
        services.AddSingleton<BackupService>();
        return services;
    }
}
