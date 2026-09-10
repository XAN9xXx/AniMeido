namespace AniMeido.Contracts;

/// <summary>
/// 插件向宿主请求页面导航的最小接口。
/// 宿主（App 层 NavigationService）实现此接口并注入给插件。
/// </summary>
public interface IPluginNavigator
{
    void Navigate(Type pageType, object? parameter = null);
}
