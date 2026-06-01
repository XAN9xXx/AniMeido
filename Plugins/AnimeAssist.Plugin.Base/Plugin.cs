using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Plugin.Base
{
    /// <summary>
    /// AniMeido 基础插件，提供季度新番浏览与番剧详情查询功能。
    /// </summary>
    public class BasePlugin : IPlugin
    {
        public string PluginID => "AniMeido.Plugin.Base";

        public string DisplayName => "基础插件";

        public string Version => "1.1.0";

        public bool IsRequired => true;

        /// <summary>
        /// 创建此插件需要注册的导航栏项
        /// </summary>
        /// <returns>可枚举的导航栏列表</returns>
        public IEnumerable<PluginNavigationItem> GetNavigationItems()
        {
            return new List<PluginNavigationItem>
            {
                //TODO: 这里的\uE713需要改成实际的导航栏logo
                new PluginNavigationItem("正在放送", "\uE713", "AniMeido.Plugin.Base.Views.CurrentSeasonPage"),
                new PluginNavigationItem("补番计划", "\uE713", "AniMeido.Plugin.Base.Views.PastSeasonPage"),
                new PluginNavigationItem("搜索", "\uE71A", "AniMeido.Plugin.Base.Views.GlobalSearchPage"),
                new PluginNavigationItem("关注管理", "\uE72E", "AniMeido.Plugin.Base.Views.ManagementPage"),
                new PluginNavigationItem("拖放标记", "\uE713", "AniMeido.Plugin.Base.Views.DragZoneSettingsPage")
                {
                    IsSettingsPage = true
                }
            };
        }

        /// <summary>
        /// DI注入服务
        /// </summary>
        /// <param name="services"></param>
        /// <returns>Task完成标记</returns>
        public Task InitializeAsync(IServiceCollection services)
        {
            services.AddBangumiService();
            services.AddSingleton<ExportService>();
            services.AddSingleton<SavedTagService>();
            services.AddSingleton<LocalSearchService>(sp =>
                new LocalSearchService(
                    sp.GetRequiredService<TrackingService>(),
                    sp.GetRequiredService<IAnimeDataSource>(),
                    sp.GetRequiredService<CacheService>()));
            return Task.CompletedTask;
        }
    }
}
