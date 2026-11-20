using Microsoft.UI.Dispatching;

namespace AniMeido.App.Services;

internal static class DesktopWindowClose
{
    internal static void Enqueue(DispatcherQueue dispatcher, Action close)
    {
        // Uno 的原生关闭回调尚在执行时会跳过 HWND 销毁，最终关闭必须排到回调返回之后。
        if (!dispatcher.TryEnqueue(() => close()))
            throw new InvalidOperationException("界面队列已停止，无法完成窗口关闭。");
    }
}
