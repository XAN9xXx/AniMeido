using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AniMeido.App.Services;

/// <summary>
/// 插件启动逻辑。负责加载内置插件和动态插件，收集导航项。
/// 使用 Serilog 静态 Log 获取 logger，避免构建临时 ServiceProvider。
/// </summary>
internal static class PluginStartup
{
    /// <summary>加载所有插件并返回导航项列表和插件列表。</summary>
    public static async Task<(List<PluginNavigationItem> NavItems, IReadOnlyList<IPlugin> Plugins)> LoadPluginsAsync(
        IServiceCollection services,
        PluginPackageManager packageManager)
    {
        // 使用 Serilog 静态 Log 构建 logger，避免为获取 ILogger 而构建临时 ServiceProvider
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(dispose: false);
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        });
        var logger = loggerFactory.CreateLogger<PluginHost>();
        var host = new PluginHost(services, logger);

        var navItems = new List<PluginNavigationItem>();
        navItems.AddRange(await host.LoadBuiltInPluginAsync(new Plugin.Base.BasePlugin()));
        try
        {
            var pluginDirectories = await packageManager.PrepareForStartupAsync();
            navItems.AddRange(await host.LoadPluginDirectoriesAsync(pluginDirectories));
            await packageManager.RecordLoadFailuresAsync(host.GetLoadFailures());
        }
#pragma warning disable CA1031 // Optional plugin state must not prevent base application startup.
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Plugin registry could not be prepared. Optional plugins were skipped.");
        }
#pragma warning restore CA1031

        return (navItems, host.GetPlugins());
    }
}
