using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace AniMeido.Plugin.Player.Sources.Web;

/// <summary>一个窗口的网页宿主身份与 UI 执行能力；有效性由 Resolver 的生命周期锁保护。</summary>
internal sealed class WebViewHostBinding
{
    private readonly Func<Func<Task>, CancellationToken, Task> _dispatch;

    internal WebViewHostBinding(nint windowHandle, Func<Func<Task>, CancellationToken, Task> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (windowHandle == nint.Zero)
            throw new ArgumentException("网页宿主窗口句柄不能为空。", nameof(windowHandle));
        WindowHandle = windowHandle;
        _dispatch = dispatch;
    }

    internal nint WindowHandle { get; }
    internal bool IsActive { get; private set; } = true;
    internal void Invalidate() => IsActive = false;

    internal static WebViewHostBinding FromWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var dispatcher = window.DispatcherQueue;
        return new WebViewHostBinding(WindowNative.GetWindowHandle(window),
            (action, token) => DispatchAsync(dispatcher, action, token));
    }

    // 失效禁止新业务，不禁止该窗口执行自己尚未完成的清理。
    internal Task InvokeAsync(Func<Task> action, CancellationToken token)
        => _dispatch(action, token);

    internal async Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        T result = default!;
        await _dispatch(async () =>
        {
            result = await action();
        }, token);
        return result;
    }

    private static async Task DispatchAsync(DispatcherQueue dispatcher, Func<Task> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (dispatcher.HasThreadAccess)
        {
            await action();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                await action();
                completion.TrySetResult();
            }
#pragma warning disable CA1031 // UI 回调把原异常交还真实等待方，不提前结束宿主操作。
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
#pragma warning restore CA1031
        }))
        {
            throw new InvalidOperationException("无法调度网页组件操作。");
        }
        await completion.Task;
    }
}
