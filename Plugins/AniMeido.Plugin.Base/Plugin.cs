using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using AniMeido.Contracts.Playback;
using AniMeido.Contracts.Desktop;
using AniMeido.Contracts.PersonalAnime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Base
{
    public class BasePlugin : IPlugin, IAsyncDisposable
    {
        public string PluginID => "AniMeido.Plugin.Base";

        public string DisplayName => "基础插件";

        public string Version =>
            typeof(BasePlugin).Assembly.GetName().Version?.ToString(3)
            ?? throw new InvalidOperationException("BasePlugin 程序集缺少版本。");

        public bool IsRequired => true;

        public ValueTask DisposeAsync() => ImageCacheHelper.StopAsync();

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
                new PluginNavigationItem("我的番剧", "\uE728", "AniMeido.Plugin.Base.Views.ManagementPage")
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

        public Task InitializeAsync(IServiceCollection services)
        {
            services.AddSingleton<SqliteConnectionFactory>();
            services.AddMetadataService();
            services.AddSingleton<TagSearchResults>();
            services.AddSingleton<ExportService>();
            services.AddSingleton<SavedTagService>();
            services.AddSingleton<DragDropService>();
            services.AddSingleton<ActionCenterService>();
            services.AddSingleton<ArchiveService>();
            services.AddSingleton<ArchiveQueryService>();
            services.AddSingleton<ArchiveReviewExporter>();
            services.AddSingleton<IScreenshotRecycleBin, WindowsScreenshotRecycleBin>();
            services.AddSingleton<ScreenshotArchiveService>();
            services.AddSingleton<ArchiveBundleService>();
            services.AddSingleton<PersonalDataTransferService>();
            services.AddSingleton<RecommendationCandidateProvider>();
            services.AddSingleton<RecommendationService>();
            services.AddSingleton<AniMeido.Plugin.Base.Models.RecommendationBrowseState>();
            services.AddSingleton<PersonalAnimeDataGateway>();
            services.AddSingleton<IPersonalAnimeDataGateway>(provider =>
                provider.GetRequiredService<PersonalAnimeDataGateway>());
            services.AddSingleton<ScreenshotShortcutAction>();
            services.AddSingleton<IGlobalShortcutAction>(provider =>
                provider.GetRequiredService<ScreenshotShortcutAction>());
            services.AddSingleton<IAnimePlaybackProgressSink>(provider =>
                provider.GetRequiredService<ActionCenterService>());
            services.AddSingleton<IPlanReminderNavigation, PlanReminderNavigation>();
            services.AddSingleton<PlanReminderCoordinator>();
            services.AddSingleton<LocalSearchService>(sp =>
                new LocalSearchService(
                    sp.GetRequiredService<TrackingService>(),
                    sp.GetRequiredService<IAnimeDataSource>(),
                    sp.GetRequiredService<ILogger<LocalSearchService>>()));
            return Task.CompletedTask;
        }
    }
}
