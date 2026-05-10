using AnimeAssist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/*
 * xxxPlugin插件下的Plugin.cs文件是插件的入口点，必须实现IPlugin接口。
 * 通过实现该接口，插件可以向主应用程序提供必要的信息和功能。
 */


namespace AnimeAssist.Plugin.Base
{
    public class BasePlugin : IPlugin
    {
        public string PID => "AnimeAssist.Plugin.Base";

        public string DisplayName => "当季新番";

        public string Version => "V1.0.0";

        public bool IsRequired => true;

        public IEnumerable<PluginNavigationItem> GetNavigationItems()
        {
            return new List<PluginNavigationItem>
            {
                new PluginNavigationItem("当季新番", "🤔", "AnimeAssist.Plugin.Base.Views.CurrentSeasonsPage"),
                new PluginNavigationItem("补番计划", "🤔", "AnimeAssist.Plugin.Base.Views.PastSeasonsPage")
            };
        }

        public Task InitializeAsync(IServiceCollection services)
        {
            return Task.CompletedTask;
        }
    }
}
