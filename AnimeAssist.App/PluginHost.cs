using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.Loader;

// TODO:PersonWork.cs已创建但未实现，为排除编译错误，暂时“从项目中排除”

namespace AniMeido.App
{
    /// <summary>
    /// 自定义 AssemblyLoadContext，从指定子目录加载插件程序集及其依赖，
    /// 让 WinRT/WinUI 原生层能正确解析插件资源。
    /// </summary>
    internal class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _pluginDir;

        public PluginLoadContext(string pluginDir) : base(isCollectible: true)
        {
            _pluginDir = pluginDir;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
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

        internal PluginHost(IServiceCollection services, ILogger<PluginHost> logger)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(logger);
            _services = services;
            _logger = logger;
            _plugins = new List<IPlugin>();
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
                    await plugin.InitializeAsync(_services);
                    _plugins.Add(plugin);
                    items.AddRange(plugin.GetNavigationItems());
                }
                return items;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error : {message} Invalid dll files found in Plugins folder", ex.Message);
                return items;
            }
        }

        /// <summary>
        /// 返回已加载的插件列表。
        /// </summary>
        public IReadOnlyList<IPlugin> GetPlugins() => _plugins.AsReadOnly();

        /// <summary>
        /// 1. 扫描已加载的程序集（编译期依赖插件，如 BasePlugin）
        /// 2. 扫描根目录下 Plugins\ 子目录中的 *.dll（动态加载插件）
        /// </summary>
        public async Task<IReadOnlyList<PluginNavigationItem>> LoadPluginAsync(string path)
        {
            List<PluginNavigationItem> items = new List<PluginNavigationItem>();

            if (!Directory.Exists(path))
            {
                _logger.LogError("目录 {Path} 不存在", path);
                return items;
            }

            // 1. 编译期依赖插件：从已加载的程序集中查找 IPlugin 实现
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var pluginTypes = assembly.GetExportedTypes()
                        .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
                    foreach (var type in pluginTypes)
                    {
                        var plugin = (IPlugin)Activator.CreateInstance(type)!;
                        await plugin.InitializeAsync(_services);
                        _plugins.Add(plugin);
                        items.AddRange(plugin.GetNavigationItems());
                    }
                }
                catch
                {
                    // 跳过无法读取类型的程序集
                }
            }

            // 2. 动态加载插件：path\Plugins\ 下的各子目录
            var pluginsDir = Path.Combine(path, "Plugins");
            if (Directory.Exists(pluginsDir))
            {
                foreach (var pluginDir in Directory.GetDirectories(pluginsDir))
                {
                    var dllFiles = Directory.GetFiles(pluginDir, "*.dll");
                    if (dllFiles.Length == 0)
                    {
                        _logger.LogWarning("插件目录 {Dir} 中未找到 DLL 文件", pluginDir);
                        continue;
                    }
                    foreach (var dllPath in dllFiles)
                    {
                        var dllItems = await LoadDllFile(dllPath);
                        items.AddRange(dllItems);
                    }
                }
            }

            return items;
        }
    }
}