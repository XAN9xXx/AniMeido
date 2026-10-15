using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Base.Services
{
    public static class BangumiServiceExtensions
    {
        public static IServiceCollection AddBangumiService(this IServiceCollection services)
        {
            services.AddHttpClient(BangumiApiClient.ArchiveClientName, client =>
            {
                client.BaseAddress = new Uri("https://archive.animeido.com:38443");
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "XAN9xXx/AniMeido/2.1.0 (https://github.com/XAN9xXx/AniMeido)");
            });
            services.AddHttpClient(BangumiApiClient.FallbackClientName, client =>
            {
                client.BaseAddress = new Uri("https://bgm-proxy.animeido.com");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "XAN9xXx/AniMeido/2.1.0 (https://github.com/XAN9xXx/AniMeido)");
            });
            services.AddSingleton<IAnimeDataSource>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<BangumiDataSource>>();
                var apiClient = sp.GetRequiredService<BangumiApiClient>();
                var cache = sp.GetRequiredService<CacheService>();
                return new BangumiDataSource(logger, apiClient, cache);
            });
            services.AddSingleton<BangumiApiClient>();
            services.AddSingleton<TrackingService>();
            services.AddSingleton<CacheService>();
            services.AddSingleton<BrowseHistoryService>();
            services.AddSingleton<BackupService>();
            return services;
        }
    }
}
