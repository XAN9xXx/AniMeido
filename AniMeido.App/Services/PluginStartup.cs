using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.App.Services;

/// <summary>
/// 初始化进程内 Base 插件的服务并收集导航项；可选插件由 PluginHostSupervisor 管理。
/// </summary>
internal static class PluginStartup
{
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
