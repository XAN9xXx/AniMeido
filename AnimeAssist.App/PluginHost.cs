using AniMeido.App.Models;
using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.Loader;

namespace AniMeido.App;

internal static class SharedAssemblyNames
{
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "AniMeido.Contracts",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Primitives",
        "Microsoft.Extensions.Http",
    };
}

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginDirectory)
        : base(isCollectible: false)
    {
        _pluginDirectory = pluginDirectory;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null
            && SharedAssemblyNames.Names.Contains(assemblyName.Name))
        {
            return null;
        }

        var path = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}

public sealed class PluginHost
{
    private readonly ILogger<PluginHost> _logger;
    private readonly IServiceCollection _services;
    private readonly List<IPlugin> _plugins = [];
    private readonly List<PluginLoadContext> _loadContexts = [];
    private readonly PluginIdTracker _pluginIds = new();
    private readonly Dictionary<string, string> _loadFailures =
        new(StringComparer.OrdinalIgnoreCase);

    internal PluginHost(
        IServiceCollection services,
        ILogger<PluginHost> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _logger = logger;
    }

    internal async Task<IReadOnlyList<PluginNavigationItem>> LoadBuiltInPluginAsync(
        IPlugin plugin)
    {
        if (!_pluginIds.TryAdd(plugin.PluginID))
        {
            _logger.LogWarning(
                "Plugin {PluginID} is already loaded. Skipped.",
                plugin.PluginID);
            return [];
        }

        await plugin.InitializeAsync(_services);
        _plugins.Add(plugin);
        return plugin.GetNavigationItems().ToList();
    }

    internal async Task<IReadOnlyList<PluginNavigationItem>> LoadPluginDirectoriesAsync(
        IEnumerable<string> pluginDirectories)
    {
        var items = new List<PluginNavigationItem>();
        foreach (var pluginDirectory in pluginDirectories)
        {
            var pluginItems = await LoadPluginDirectoryAsync(pluginDirectory);
            items.AddRange(pluginItems);
        }

        return items;
    }

    public IReadOnlyList<IPlugin> GetPlugins() => _plugins.AsReadOnly();

    public IReadOnlyDictionary<string, string> GetLoadFailures() => _loadFailures;

    private async Task<IReadOnlyList<PluginNavigationItem>> LoadPluginDirectoryAsync(
        string pluginDirectory)
    {
        string? reservedPluginId = null;
        try
        {
            var manifest = PluginManifest.LoadFromFile(
                Path.Combine(pluginDirectory, "plugin.json"))
                ?? throw new PluginOperationException("插件目录缺少 plugin.json。");
            var entryAssemblyPath = PluginPackageVerifier.ResolveSafePath(
                pluginDirectory,
                manifest.EntryAssembly);
            if (!File.Exists(entryAssemblyPath))
            {
                throw new PluginOperationException("插件入口程序集不存在。");
            }

            if (!_pluginIds.TryAdd(manifest.PluginId))
            {
                throw new PluginOperationException($"插件 ID 重复：{manifest.PluginId}");
            }

            reservedPluginId = manifest.PluginId;
            var loadContext = new PluginLoadContext(pluginDirectory);
            var assembly = loadContext.LoadFromAssemblyPath(entryAssemblyPath);
            var pluginTypes = assembly
                .GetExportedTypes()
                .Where(type =>
                    typeof(IPlugin).IsAssignableFrom(type)
                    && type.IsClass
                    && !type.IsAbstract)
                .ToList();
            if (pluginTypes.Count != 1)
            {
                throw new PluginOperationException(
                    "每个插件包必须包含且仅包含一个公开 IPlugin 实现。");
            }

            if (Activator.CreateInstance(pluginTypes[0]) is not IPlugin plugin)
            {
                throw new PluginOperationException("无法创建插件入口实例。");
            }

            ValidateRuntimeIdentity(manifest, plugin);
            var pluginServices = new ServiceCollection();
            await plugin.InitializeAsync(pluginServices);
            var navigationItems = plugin.GetNavigationItems().ToList();

            foreach (var service in pluginServices)
            {
                _services.Add(service);
            }

            _loadContexts.Add(loadContext);
            _plugins.Add(plugin);
            _logger.LogInformation(
                "Loaded plugin {PluginID} v{Version} from {PluginDirectory}.",
                plugin.PluginID,
                plugin.Version,
                pluginDirectory);
            return navigationItems;
        }
#pragma warning disable CA1031 // An optional plugin must not prevent base application startup.
        catch (Exception ex)
        {
            if (reservedPluginId is not null)
            {
                _pluginIds.Remove(reservedPluginId);
                _loadFailures[reservedPluginId] = ex.Message;
            }

            _logger.LogError(
                ex,
                "Failed to load plugin from {PluginDirectory}.",
                pluginDirectory);
            return [];
        }
#pragma warning restore CA1031
    }

    private static void ValidateRuntimeIdentity(
        PluginManifest manifest,
        IPlugin plugin)
    {
        if (!string.Equals(
            manifest.PluginId,
            plugin.PluginID,
            StringComparison.Ordinal))
        {
            throw new PluginOperationException(
                "插件清单 ID 与运行时 PluginID 不一致。");
        }

        if (!string.Equals(
            manifest.Version,
            plugin.Version,
            StringComparison.Ordinal))
        {
            throw new PluginOperationException(
                "插件清单版本与运行时插件版本不一致。");
        }

        if (plugin.IsRequired)
        {
            throw new PluginOperationException(
                "外部插件不能声明为 required 插件。");
        }

        if (string.IsNullOrWhiteSpace(plugin.DisplayName))
        {
            throw new PluginOperationException("插件显示名称不能为空。");
        }
    }

}
