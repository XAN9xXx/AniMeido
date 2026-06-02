using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Contracts
{
    /// <summary>
    /// 将插件提供的导航项信息封装为一个记录类型，包含显示名称、图标、页面类型。
    /// </summary>
    /// <param name="Label">导航栏显示名称。</param>
    /// <param name="Icon">导航栏图标。</param>
    /// <param name="PageTypeName">页面类型名称（字符串形式，兼容旧版反射导航）。</param>
    public record PluginNavigationItem(string Label, string Icon, string PageTypeName)
    {
        /// <summary>页面类型。由插件在返回导航项时填充，用于编译期安全的导航。</summary>
        public Type? PageType { get; init; }

        /// <summary>是否为插件设置页面。设置页不会显示在主导航中，而是聚合到设置页内。</summary>
        public bool IsSettingsPage { get; init; }
    }

    /// <summary>
    /// 定义插件的基本接口，所有插件必须实现此接口以便被主应用程序识别和加载。接口包含插件的唯一标识符、显示名称、版本信息、是否为必需插件、初始化方法以及获取导航项列表的方法。
    /// </summary>
    /// <remarks>
    /// 必需插件(<see cref="IsRequired"/> 返回 <c>true</c>)无法被用户卸载。
    /// 插件在加载时会调用 InitializeAsync 方法，传入一个 IServiceCollection 参数，插件可以在此方法中注册自己的服务和依赖项。
    /// </remarks>
    public interface IPlugin
    {
        /// <summary>插件的唯一标识符。</summary>
        string PluginID { get; }

        /// <summary>插件的显示名称。</summary>
        string DisplayName { get; }

        /// <summary>插件的版本。</summary>
        string Version { get; }

        /// <summary>是否为必需插件。</summary>
        bool IsRequired { get; }

        /// <summary>
        /// 插件初始化方法。在插件加载时调用，用于注册服务和依赖项。
        /// </summary>
        /// <param name="services">依赖注入服务集合。</param>
        Task InitializeAsync(IServiceCollection services);

        /// <summary>
        /// 插件导航项，包含显示名称、图标和目标页面类型。
        /// </summary>
        /// <returns>
        /// 导航项集合。
        /// </returns>
        IEnumerable<PluginNavigationItem> GetNavigationItems();
    }
}

