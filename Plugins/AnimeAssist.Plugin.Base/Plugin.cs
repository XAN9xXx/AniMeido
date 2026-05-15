using AnimeAssist.Contracts;
using AnimeAssist.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;

/*
 * xxxPlugin插件下的Plugin.cs文件是插件的入口点，必须实现IPlugin接口。
 * 通过实现该接口，插件可以向主应用程序提供必要的信息和功能。
 */


namespace AnimeAssist.Plugin.Base
{
    public class BasePlugin : IPlugin
    {
        public string PluginID => "AnimeAssist.Plugin.Base";

        public string DisplayName => "当季新番";

        public string Version => "1.0.0";

        public bool IsRequired => true;

        public IEnumerable<PluginNavigationItem> GetNavigationItems()
        {
            return new List<PluginNavigationItem>
            {
                //TODO: 这里的🤔需要改成实际的导航栏logo，暂时用emoji占位
                new PluginNavigationItem("当季新番", "🤔", "AnimeAssist.Plugin.Base.Views.CurrentSeasonPage"),
                new PluginNavigationItem("补番计划", "🤔", "AnimeAssist.Plugin.Base.Views.PastSeasonPage")
            };
        }

        public Task InitializeAsync(IServiceCollection services)
        {
            services.AddBangumiService();
            return Task.CompletedTask;
        }
    }
}
