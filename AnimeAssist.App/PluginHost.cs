using AniMeido.App.Models;
using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.Loader;

namespace AniMeido.App
{
    /// <summary>
    /// 自定义 AssemblyLoadContext，从指定子目录加载插件程序集及其依赖，
    /// 让 WinRT/WinUI 原生层能正确解析插件资源。
    /// </summary>
    /// <summary>
    /// 宿主共享程序集名称列表。插件目录中存在同名 DLL 时，不使用插件版本。
    /// 避免 AssemblyLoadContext 类型身份不匹配导致 IPlugin 无法识别。
    /// </summary>
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

    internal class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _pluginDir;

        public PluginLoadContext(string pluginDir) : base(isCollectible: true)
        {
            _pluginDir = pluginDir;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 共享程序集回退到默认上下文，避免类型身份分裂
            if (assemblyName.Name != null && SharedAssemblyNames.Names.Contains(assemblyName.Name))
                return null;

            var path = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
            if (File.Exists(path))
                return LoadFromAssemblyPath(path);
            return null; // 回退到默认加载上下文
        }
    }

    public class PluginHost
    {
        private readonly ILogger<PluginHost> _logger;
        private readonly IServiceCollection _services;
        private readonly List<IPlugin> _plugins;
        private readonly PluginIdTracker _pluginIds = new();

        internal PluginHost(IServiceCollection services, ILogger<PluginHost> logger)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(logger);
            _services = services;
            _logger = logger;
            _plugins = new List<IPlugin>();
        }

        /// <summary>
        /// 显式加载 built-in plugin，返回其导航项。以 PluginID 去重。
        /// </summary>
        internal async Task<IReadOnlyList<PluginNavigationItem>> LoadBuiltInPluginAsync(IPlugin plugin)
        {
            if (!_pluginIds.TryAdd(plugin.PluginID))
            {
                _logger.LogWarning("Plugin {PluginID} is already loaded. Skipped.", plugin.PluginID);
                return Array.Empty<PluginNavigationItem>();
            }

            await plugin.InitializeAsync(_services);
            _plugins.Add(plugin);
            return plugin.GetNavigationItems().ToList();
        }

        /// <summary>
        /// 从指定的 dll 文件反射加载 IPlugin 实现类，
        /// 触发各插件的 InitializeAsync 以注册服务，
        /// 并收集所有插件提供的导航项。
        /// </summary>
        /// <param name="dllPath">dll文件路径。</param>
        /// <returns>插件的导航信息集合。</returns>
        internal async Task<IReadOnlyList<PluginNavigationItem>> LoadDllFile(string dllPath)
        {

            List<PluginNavigationItem> items = new List<PluginNavigationItem>();
            try
            {
                // 反射加载dll，筛选IPlugin实现类
                string? pluginDir = System.IO.Path.GetDirectoryName(dllPath);

                // ==== 第一步：在加载 DLL 之前验证 plugin.json ====
                string? manifestPluginId = null;
                string? manifestVersion = null;
                if (pluginDir != null)
                {
                    var manifestPath = System.IO.Path.Combine(pluginDir, "plugin.json");
                    var manifest = PluginManifest.LoadFromFile(manifestPath);
                    if (manifest != null)
                    {
                        // 验证 entryAssembly 与实际 DLL 文件名一致
                        var dllFileName = System.IO.Path.GetFileName(dllPath);
                        if (!string.Equals(manifest.EntryAssembly, dllFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogError("plugin.json entryAssembly ({Manifest}) 与实际 DLL ({Dll}) 不匹配",
                                manifest.EntryAssembly, dllFileName);
                            return items;
                        }

                        // 验证 MinAppVersion
                        var appVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version;
                        if (appVersion != null && Version.TryParse(manifest.MinAppVersion, out var minVer))
                        {
                            if (appVersion < minVer)
                            {
                                _logger.LogError("插件 {PluginId} 要求 App 版本 >= {MinVer}, 当前版本 {CurVer}",
                                    manifest.PluginId, manifest.MinAppVersion, appVersion);
                                return items;
                            }
                        }

                        // 在加载前先验证签名（hash 和 signature）
                        if (!PluginSignatureVerifier.Verify(manifest, dllPath, _logger))
                        {
                            _logger.LogError("插件签名验证失败，拒绝加载: {DllPath}", dllPath);
                            return items;
                        }

                        manifestPluginId = manifest.PluginId;
                        manifestVersion = manifest.Version;
                        _logger.LogInformation("插件清单验证通过: {PluginId} v{Version}",
                            manifest.PluginId, manifest.Version);
                    }
                    else
                    {
                        // 没有 manifest — 已配公钥则拒绝
                        if (PluginSignatureVerifier.IsConfigured)
                        {
                            _logger.LogError("插件目录缺少 plugin.json 或格式无效，拒绝加载: {DllPath}", dllPath);
                            return items;
                        }
                        _logger.LogWarning("插件目录缺少 plugin.json（开发模式）, {DllPath}", dllPath);
                    }
                }

                // ==== 第二步：通过验证后再加载程序集 ====
                Assembly assembly;
                if (pluginDir != null && pluginDir != AppContext.BaseDirectory)
                {
                    // 子目录加载：使用 PluginLoadContext 确保 WinRT 资源可解析
                    var context = new PluginLoadContext(pluginDir);
                    assembly = context.LoadFromAssemblyPath(dllPath);
                }
                else
                {
                    // 根目录加载：按名称加载到默认上下文，与 ProjectReference 共享类型
                    var assemblyName = System.Reflection.AssemblyName.GetAssemblyName(dllPath);
                    assembly = Assembly.Load(assemblyName);
                }

                _logger.LogInformation("成功加载插件程序集: {DllPath}", dllPath);

                Type[] allTypes = assembly.GetExportedTypes();
                var pluginTypes = allTypes
                    .Where(type =>
                    typeof(IPlugin).IsAssignableFrom(type) &&
                    type.IsClass &&
                    !type.IsAbstract);

                // 为每个插件类创建实例并初始化，收集导航项
                foreach (var type in pluginTypes)
                {
                    object obj = Activator.CreateInstance(type)!;
                    IPlugin plugin = (IPlugin)obj;

                    // 基本自检：验证插件必需字段非空
                    if (string.IsNullOrWhiteSpace(plugin.PluginID))
                    {
                        _logger.LogWarning("Plugin from {DllPath} has empty PluginID. Skipped.", dllPath);
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(plugin.DisplayName))
                    {
                        _logger.LogWarning("Plugin {PluginID} from {DllPath} has empty DisplayName. Skipped.", plugin.PluginID, dllPath);
                        continue;
                    }

                    // Manifest 身份绑定校验：manifest 字段与运行时 IPlugin 一致
                    if (manifestPluginId != null && !string.Equals(manifestPluginId, plugin.PluginID, StringComparison.Ordinal))
                    {
                        _logger.LogError("manifest pluginId ({ManifestId}) 与运行时 PluginID ({RuntimeId}) 不匹配",
                            manifestPluginId, plugin.PluginID);
                        continue;
                    }

                    _logger.LogInformation("Loading plugin: {PluginID} v{Version} - {DisplayName}",
                        plugin.PluginID, plugin.Version ?? "0.0", plugin.DisplayName);

                    if (!_pluginIds.TryAdd(plugin.PluginID))
                    {
                        _logger.LogWarning("Plugin {PluginID} is already loaded. Skipped.", plugin.PluginID);
                        continue;
                    }
                    await plugin.InitializeAsync(_services);
                    _plugins.Add(plugin);
                    items.AddRange(plugin.GetNavigationItems());
                }
                return items;
            }
#pragma warning disable CA1031 // 插件加载失败不应阻止其他插件
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载插件 DLL 失败: {DllPath}", dllPath);
#pragma warning restore CA1031
                return items;
            }
        }

        /// <summary>
        /// 返回已加载的插件列表。
        /// </summary>
        public IReadOnlyList<IPlugin> GetPlugins() => _plugins.AsReadOnly();

        /// <summary>
        /// 从 Plugins 子目录动态加载外部插件 DLL。
        /// 已通过 LoadBuiltInPluginAsync 加载的插件按 PluginID 去重。
        /// </summary>
        public async Task<IReadOnlyList<PluginNavigationItem>> LoadDynamicPluginsAsync(string basePath)
        {
            List<PluginNavigationItem> items = new List<PluginNavigationItem>();

            var pluginsDir = Path.Combine(basePath, "Plugins");
            if (!Directory.Exists(pluginsDir))
                return items;

            // 遍历 Plugins\ 下的各子目录
            // 约定：插件主 DLL 命名 = 目录名.dll，其他 DLL 作为依赖由 PluginLoadContext 自动解析
            foreach (var pluginDir in Directory.GetDirectories(pluginsDir))
            {
                var dirName = Path.GetFileName(pluginDir);
                var mainDll = Path.Combine(pluginDir, $"{dirName}.dll");

                if (!File.Exists(mainDll))
                {
                    _logger.LogWarning("插件目录 {Dir} 中未找到主 DLL {MainDll}，跳过", pluginDir, $"{dirName}.dll");
                    continue;
                }

                var dllItems = await LoadDllFile(mainDll);
                items.AddRange(dllItems);
            }

            return items;
        }
    }
}