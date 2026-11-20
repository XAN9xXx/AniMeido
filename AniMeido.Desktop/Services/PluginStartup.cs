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
        Exception? uiFailure = null;
        try { await DesktopUiWork.DrainAsync(); }
#pragma warning disable CA1031 // 界面失败已收集，数据库和插件仍按顺序结束。
        catch (Exception error) { uiFailure = error; }
#pragma warning restore CA1031
        await factory.StopAsync();
        await Task.WhenAll(plugins.OfType<IAsyncDisposable>()
            .Select(plugin => plugin.DisposeAsync().AsTask()));
        if (uiFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(uiFailure).Throw();
    }

    public static async Task<(List<PluginNavigationItem> NavItems, IReadOnlyList<IPlugin> Plugins)> LoadPluginsAsync(
        IServiceCollection services)
    {
        var plugin = new Plugin.Base.BasePlugin();
        var navItems = new List<PluginNavigationItem>();
        await plugin.InitializeAsync(services);
        navItems.AddRange(plugin.GetNavigationItems());
        return (navItems, new IPlugin[] { plugin });
    }
}
