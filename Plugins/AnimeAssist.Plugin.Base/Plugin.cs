using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
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
                new PluginNavigationItem("正在放送", "\uE713", "AniMeido.Plugin.Base.Views.CurrentSeasonPage")
                {
                    PageType = typeof(Views.CurrentSeasonPage)
                },
                new PluginNavigationItem("补番计划", "\uE713", "AniMeido.Plugin.Base.Views.PastSeasonPage")
                {
                    PageType = typeof(Views.PastSeasonPage)
                },
                new PluginNavigationItem("搜索", "\uE71A", "AniMeido.Plugin.Base.Views.GlobalSearchPage")
                {
                    PageType = typeof(Views.GlobalSearchPage)
                },
                new PluginNavigationItem("关注管理", "\uE72E", "AniMeido.Plugin.Base.Views.ManagementPage")
                {
                    PageType = typeof(Views.ManagementPage)
                },
                new PluginNavigationItem("浏览记录", "\uE71A", "AniMeido.Plugin.Base.Views.BrowseHistoryPage")
                {
                    PageType = typeof(Views.BrowseHistoryPage)
                },
                new PluginNavigationItem("拖放标记", "\uE713", "AniMeido.Plugin.Base.Views.DragZoneSettingsPage")
                {
                    PageType = typeof(Views.DragZoneSettingsPage),
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
            services.AddSingleton<DragDropService>();
            services.AddSingleton<LocalSearchService>(sp =>
                new LocalSearchService(
                    sp.GetRequiredService<TrackingService>(),
                    sp.GetRequiredService<IAnimeDataSource>(),
                    sp.GetRequiredService<CacheService>()));
            services.AddTransient<BrowseHistoryViewModel>();
            return Task.CompletedTask;
        }
    }
}
