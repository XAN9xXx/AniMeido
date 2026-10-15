namespace AniMeido.Contracts;

/// <summary>
/// 支持导航参数传递的页面契约。
/// 由 NavigationService 在页面挂载到 Frame 后调用，替代 WinUI Frame.Navigate 的 OnNavigatedTo 生命周期。
/// 实现为 Task 而非 void，避免 async void 异常不可控问题。
/// NavigationService 以 fire-and-forget 方式调用。
/// </summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync(object? parameter);
}
