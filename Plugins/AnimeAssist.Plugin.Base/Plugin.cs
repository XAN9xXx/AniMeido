using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using AniMeido.Contracts.Playback;
using AniMeido.Contracts.Desktop;
using AniMeido.Contracts.PersonalAnime;
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

        public string Version => "2.1.0";

        public bool IsRequired => true;

        /// <summary>
        /// 创建此插件需要注册的导航栏项
        /// </summary>
        /// <returns>可枚举的导航栏列表</returns>
        public IEnumerable<PluginNavigationItem> GetNavigationItems()
        {
            return new List<PluginNavigationItem>
            {
                new PluginNavigationItem("放送日历", "\uE787", "AniMeido.Plugin.Base.Views.CurrentSeasonPage")
                {
                    PageType = typeof(Views.CurrentSeasonPage)
                },
                new PluginNavigationItem("今天", "\uEC92", "AniMeido.Plugin.Base.Views.TodayPage")
                {
                    PageType = typeof(Views.TodayPage)
                },
                new PluginNavigationItem("推荐", "\uE734", "AniMeido.Plugin.Base.Views.RecommendationPage")
                {
                    PageType = typeof(Views.RecommendationPage)
                },
                new PluginNavigationItem("档案馆", "\uE8F1", "AniMeido.Plugin.Base.Views.ArchivePage")
                {
                    PageType = typeof(Views.ArchivePage)
                },
                new PluginNavigationItem("番剧库", "\uE916", "AniMeido.Plugin.Base.Views.PastSeasonPage")
                {
                    PageType = typeof(Views.PastSeasonPage)
                },
                new PluginNavigationItem("搜索", "\uE721", "AniMeido.Plugin.Base.Views.GlobalSearchPage")
                {
                    PageType = typeof(Views.GlobalSearchPage)
                },
                new PluginNavigationItem("关注管理", "\uE734", "AniMeido.Plugin.Base.Views.ManagementPage")
                {
                    PageType = typeof(Views.ManagementPage)
                },
                new PluginNavigationItem("拖放标记", "\uE777", "AniMeido.Plugin.Base.Views.DragZoneSettingsPage")
                {
                    PageType = typeof(Views.DragZoneSettingsPage),
                    IsSettingsPage = true
                },
                new PluginNavigationItem("数据管理", "\uE74E", "AniMeido.Plugin.Base.Views.DataManagementSettingsPage")
                {
                    PageType = typeof(Views.DataManagementSettingsPage),
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
            services.AddSingleton<SqliteConnectionFactory>();
            services.AddBangumiService();
            services.AddSingleton<ExportService>();
            services.AddSingleton<SavedTagService>();
            services.AddSingleton<DragDropService>();
            services.AddSingleton<ActionCenterService>();
            services.AddSingleton<ArchiveService>();
            services.AddSingleton<ScreenshotArchiveService>();
            services.AddSingleton<ArchiveBundleService>();
            services.AddSingleton<RecommendationCandidateProvider>();
            services.AddSingleton<RecommendationService>();
            services.AddSingleton<PersonalAnimeDataGateway>();
            services.AddSingleton<IPersonalAnimeDataGateway>(provider =>
                provider.GetRequiredService<PersonalAnimeDataGateway>());
            services.AddSingleton<ScreenshotShortcutAction>();
            services.AddSingleton<IGlobalShortcutAction>(provider =>
                provider.GetRequiredService<ScreenshotShortcutAction>());
            services.AddSingleton<IAnimePlaybackProgressSink>(provider =>
                provider.GetRequiredService<ActionCenterService>());
            services.AddSingleton<PlanReminderCoordinator>();
            services.AddSingleton<LocalSearchService>(sp =>
                new LocalSearchService(
                    sp.GetRequiredService<TrackingService>(),
                    sp.GetRequiredService<IAnimeDataSource>()));
            return Task.CompletedTask;
        }
    }
}
