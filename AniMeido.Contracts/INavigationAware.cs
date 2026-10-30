namespace AniMeido.Contracts;

/// <summary>接收新建页面的导航参数。</summary>
/// <remarks>
/// 宿主挂载新页面后启动调用，不等待异步加载完成才结束导航。
/// 从返回栈恢复同一页面实例时，不再调用此入口。
/// 需要恢复加载或刷新状态的页面，应在视图重新激活时处理。
/// </remarks>
public interface INavigationAware
{
    /// <summary>处理本次新页面的导航参数和初始加载。</summary>
    /// <param name="parameter">导航方传入的参数；没有参数时为 null。</param>
    /// <returns>页面对本次导航的异步处理；宿主负责观察未处理的异常。</returns>
    Task OnNavigatedToAsync(object? parameter);
}
