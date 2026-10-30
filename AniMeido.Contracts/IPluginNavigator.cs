namespace AniMeido.Contracts;

/// <summary>
/// 插件向宿主请求页面导航的最小接口。
/// 宿主（App 层 NavigationService）实现此接口并注入给插件。
/// </summary>
public interface IPluginNavigator
{
    /// <summary>在界面线程请求进入新页面，并将当前页面保留在返回栈。</summary>
    void Navigate(Type pageType, object? parameter = null);
}
