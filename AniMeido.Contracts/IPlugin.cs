using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Contracts
{
    /// <summary>
    /// 插件导航项入口类型。Page 用于主窗口 Frame 内页面导航，Command 用于执行插件提供的自定义操作。
    /// </summary>
    public enum PluginNavigationItemKind
    {
        Page,
        Command,
    }

    /// <param name="PageTypeName">
    /// 页面类型名称。仅当 <see cref="Kind"/> 为 Page 时有效，Command 类型可为 null。
    /// </param>
    public record PluginNavigationItem(string Label, string Icon, string? PageTypeName)
    {
        /// <summary>页面类型。由插件在返回导航项时填充，用于编译期安全的导航。</summary>
        public Type? PageType { get; init; }

        /// <summary>是否为插件设置页面。设置页不会显示在主导航中，而是聚合到设置页内。</summary>
        public bool IsSettingsPage { get; init; }

        public PluginNavigationItemKind Kind { get; init; } = PluginNavigationItemKind.Page;

        /// <summary>
        /// 命令入口的执行命令。仅当 <see cref="Kind"/> 为 Command 时有效。
        /// 使用 <c>System.Windows.Input.ICommand</c> 而非具体实现，
        /// 插件可使用 CommunityToolkit.Mvvm 的 RelayCommand 或 AsyncRelayCommand。
        /// </summary>
        public System.Windows.Input.ICommand? Command { get; init; }

        /// <summary>Command 类型入口的稳定标识，用于跨进程命令分派。</summary>
        public string? CommandId { get; init; }

        public static PluginNavigationItem CreatePage(string label, string icon, string pageTypeName, Type? pageType = null, bool isSettingsPage = false)
            => new(label, icon, pageTypeName)
            {
                Kind = PluginNavigationItemKind.Page,
                PageType = pageType,
                IsSettingsPage = isSettingsPage,
            };

        public static PluginNavigationItem CreateCommand(
            string label,
            string icon,
            string commandId,
            System.Windows.Input.ICommand command)
            => new(label, icon, null)
            {
                Kind = PluginNavigationItemKind.Command,
                CommandId = commandId,
                Command = command,
            };
    }

    /// <remarks>
    /// 必需插件(<see cref="IsRequired"/> 返回 <c>true</c>)无法被用户卸载。
    /// </remarks>
    public interface IPlugin
    {
        string PluginID { get; }

        string DisplayName { get; }

        string Version { get; }

        bool IsRequired { get; }

        /// <summary>在宿主构建 ServiceProvider 之前调用，用于注册插件自己的服务。</summary>
        Task InitializeAsync(IServiceCollection services);

        IEnumerable<PluginNavigationItem> GetNavigationItems();
    }
}

