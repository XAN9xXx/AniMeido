using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 用于注册BangumiDataSource到DI容器的扩展方法类。
    /// </summary>
    public static class BangumiServiceExtensions
    {
        /// <summary>
        /// 用于将BangumiDataSource注册为单例服务，并配置HttpClient以访问Bangumi API。
        /// </summary>
        /// <param name="services">依赖注入服务集合。</param>
        /// <returns>服务集合services，用于链式调用。</returns>
        public static IServiceCollection AddBangumiService(this IServiceCollection services)
        {
            services.AddHttpClient("BangumiAPI", client =>
            {
                client.BaseAddress = new Uri("https://bgm-proxy.animeido.com");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "XAN9xXx/AniMeido/1.0.0 (https://github.com/XAN9xXx/AniMeido)");
            });
            services.AddSingleton<IAnimeDataSource, BangumiDataSource>();
            services.AddSingleton<BangumiApiClient>();
            services.AddSingleton<TrackingService>();
            services.AddSingleton<CacheService>();
            return services;
        }
    }
}
