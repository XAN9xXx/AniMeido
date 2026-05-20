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

        public string Version => "1.0.0";

        public bool IsRequired => true;

        public IEnumerable<PluginNavigationItem> GetNavigationItems()
        {
            return new List<PluginNavigationItem>
            {
                //TODO: 这里的\uE714需要改成实际的导航栏logo，暂时用emoji占位
                new PluginNavigationItem("正在放送", "\uE713", "AniMeido.Plugin.Base.Views.CurrentSeasonPage"),
                new PluginNavigationItem("补番计划", "\uE713", "AniMeido.Plugin.Base.Views.PastSeasonPage")
            };
        }

        public Task InitializeAsync(IServiceCollection services)
        {
            services.AddBangumiService();
            return Task.CompletedTask;
        }
    }
}
