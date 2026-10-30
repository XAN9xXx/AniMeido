using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.App.Services;

/// <summary>
/// 装配进程内 Base 插件，并在数据操作排空后释放其资源；可选插件由 PluginHostSupervisor 管理。
/// </summary>
internal static class PluginStartup
{
    internal static async Task StopAfterDataDrainAsync(
        IReadOnlyList<IPlugin> plugins,
        SqliteConnectionFactory factory)
    {
        await factory.StopAsync();
        await Task.WhenAll(plugins.OfType<IAsyncDisposable>()
            .Select(plugin => plugin.DisposeAsync().AsTask()));
    }

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
