using System.Runtime.InteropServices;

namespace AniMeido.Plugin.Chat.Services;

/// <summary>
/// 封装 ChatWindow 所需的 Win32 窗口交互操作。
/// 当前仅实现 WS_EX_LAYERED + SetLayeredWindowAttributes 窗口级 alpha 透明。
/// </summary>
internal static class ChatWindowInteropService
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int LWA_ALPHA = 0x00000002;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>
    /// 设置窗口 alpha 透明度。通过 WS_EX_LAYERED 样式 + SetLayeredWindowAttributes 实现。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="opacityPercent">透明度百分比，范围 40~100。</param>
    public static void SetWindowOpacity(IntPtr hWnd, int opacityPercent)
    {
        if (hWnd == IntPtr.Zero)
            return;

        var clampedPercent = Math.Clamp(opacityPercent, 40, 100);
        byte alpha = (byte)(clampedPercent * 255 / 100);

        try
        {
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(hWnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            }
            SetLayeredWindowAttributes(hWnd, 0, alpha, LWA_ALPHA);
        }
        catch
        {
            // 窗口句柄已销毁时安全忽略，不抛出异常
        }
    }
}
