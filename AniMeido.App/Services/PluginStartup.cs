using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.App.Services;

/// <summary>
/// 插件启动逻辑。负责加载内置插件和动态插件，收集导航项。
/// 使用 Serilog 静态 Log 获取 logger，避免构建临时 ServiceProvider。
/// </summary>
internal static class PluginStartup
{
    /// <summary>加载所有插件并返回导航项列表和插件列表。</summary>
    public static async Task<(List<PluginNavigationItem> NavItems, IReadOnlyList<IPlugin> Plugins)> LoadPluginsAsync(
        IServiceCollection services)
    {
        var plugin = new Plugin.Base.BasePlugin();
        var navItems = new List<PluginNavigationItem>();
        await plugin.InitializeAsync(services);
        navItems.AddRange(plugin.GetNavigationItems());
        return (navItems, [plugin]);
    }
}
