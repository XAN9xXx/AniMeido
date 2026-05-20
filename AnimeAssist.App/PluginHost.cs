using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

// TODO:PersonWork.cs已创建但未实现，为排除编译错误，暂时“从项目中排除”

namespace AniMeido.App
{
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
                Assembly assembly = Assembly.LoadFrom(dllPath);
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
        /// 异步扫描指定目录中的所有 *.dll 程序集，加载插件并返回插件导航项集合。
        /// </summary>
        /// <remarks>如果目录不存在，会记录错误并返回空集合。逐个对目录中的每个 .dll 调用 LoadDllFile 并聚合其结果。</remarks>
        /// <param name="path">要扫描的包含插件程序集（.dll 文件）的目录路径。</param>
        /// <returns>表示从目录加载到的 PluginNavigationItem 的只读集合；若目录不存在或未找到任何插件则为空集合。</returns>
        public async Task<IReadOnlyList<PluginNavigationItem>> LoadPluginAsync(string path)
        {
            List<PluginNavigationItem> items = new List<PluginNavigationItem>();

            if (!Directory.Exists(path))
            {
                _logger.LogError("Plugins folder {Path} does not exist", path);
                return items;
            }

            foreach (var dllPath in Directory.GetFiles(path, "*.dll"))
            {
                var dllItems = await LoadDllFile(dllPath);
                items.AddRange(dllItems);
            }
            return items;
        }
    }
}