using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace AniMeido.Plugin.Base.Services;

/// <summary>
/// 跨窗口顶层 GhostCard 服务。
/// 创建一个无边框、置顶、鼠标穿透的小窗口，显示 AnimeCard 的 RenderTargetBitmap 视觉快照，
/// 跟随全局鼠标位置移动，跨越 MainWindow 与 ChatWindow。
///
/// == 定位 ==
/// - 纯视觉层，不参与数据传递、DropZone 判断、业务逻辑
/// - 替代原 overlay-based GhostCard，使 GhostCard 能跨越窗口边界
/// - DropZone 高亮仍然由 overlay 负责，不受影响
///
/// == 生命周期 ==
/// - Start() — 创建/显示窗口，启动鼠标跟踪计时器
/// - Stop() — 隐藏窗口，停止计时器
/// - 计时器每 16ms 更新位置 + 检测左键释放
/// </summary>
public sealed class AnimeCardTopLevelDragGhostService : IDisposable
{
    // ===== Win32 P/Invoke =====
    private static class Native
    {
        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("comctl32.dll")]
        internal static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, uint dwRefData);

        [DllImport("comctl32.dll")]
        internal static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass);

        [DllImport("comctl32.dll")]
        internal static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        internal delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, uint dwRefData);

        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int WS_EX_TRANSPARENT = 0x00000020;
        internal const int VK_LBUTTON = 0x01;
        internal const uint WM_NCHITTEST = 0x0084;
        internal static readonly IntPtr HTTRANSPARENT = new(-1);

        internal static readonly IntPtr HWND_TOPMOST = new(-1);
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const int SW_HIDE = 0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X; public int Y; }
    }

    private Window? _ghostWindow;
    private AppWindow? _appWindow;
    private IntPtr _hWnd;
    private DispatcherTimer? _timer;
    private bool _isRunning;
    private double _ghostWidthDip;
    private double _ghostHeightDip;
    private double _dpiScale = 1.0;
    private int _ghostWidthPx;
    private int _ghostHeightPx;
    private int _lastPosX = int.MinValue;
    private int _lastPosY = int.MinValue;
    private Native.SUBCLASSPROC? _subclassDelegate; // 防止 GC 回收

    /// <summary>拖拽被停止时触发（左键释放或外部通知）。</summary>
    public Action? OnDragStopped { get; set; }

    /// <summary>当前是否正在运行。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// 启动顶层 GhostCard。
    /// 创建或复用窗口，显示指定 ImageSource，开始鼠标跟踪。
    /// </summary>
    public void Start(ImageSource imageSource, double widthDip, double heightDip, double dpiScale = 1.0)
    {
        Stop();

        _dpiScale = dpiScale;
        _ghostWidthDip = widthDip;
        _ghostHeightDip = heightDip;
        _ghostWidthPx = Math.Max(1, (int)(widthDip * dpiScale + 0.5));
        _ghostHeightPx = Math.Max(1, (int)(heightDip * dpiScale + 0.5));
        _lastPosX = int.MinValue;
        _lastPosY = int.MinValue;

        if (_ghostWindow == null)
            CreateWindow(imageSource);
        else
            UpdateImage(imageSource);

        Show();
        StartTimer();
        _isRunning = true;

        System.Diagnostics.Debug.WriteLine("[TopLevelGhost] started");
    }

    /// <summary>
    /// 更新已运行的 GhostCard 的 ImageSource 和尺寸。
    /// 不重建窗口，不激活，不中断拖拽。
    /// 仅在 IsRunning 为 true 时有效。
    /// </summary>
    public void UpdateSource(ImageSource imageSource, double widthDip, double heightDip, double dpiScale = 1.0)
    {
        if (!_isRunning || _hWnd == IntPtr.Zero)
        {
            Start(imageSource, widthDip, heightDip, dpiScale);
            return;
        }

        _dpiScale = dpiScale;
        _ghostWidthDip = widthDip;
        _ghostHeightDip = heightDip;
        _ghostWidthPx = Math.Max(1, (int)(widthDip * dpiScale + 0.5));
        _ghostHeightPx = Math.Max(1, (int)(heightDip * dpiScale + 0.5));

        UpdateImage(imageSource);

        // 用物理像素 Resize
        if (_appWindow != null)
            _appWindow.Resize(new SizeInt32(_ghostWidthPx, _ghostHeightPx));

        System.Diagnostics.Debug.WriteLine("[TopLevelGhost] source updated");
    }

    private bool _isStopping;

    /// <summary>
    /// 停止顶层 GhostCard：隐藏窗口、停止计时器、清空回调。
    /// 不销毁窗口，下次 Start 可复用。
    /// 幂等。
    /// </summary>
    public void Stop()
    {
        if (_isStopping) return;
        _isStopping = true;
        try
        {
            _isRunning = false;
            StopTimer();
            Hide();
            OnDragStopped = null;
        }
        finally
        {
            _isStopping = false;
        }
    }

    /// <summary>
    /// 应用关闭时的完整清理：销毁窗口、移除 Win32 subclass、释放资源。
    /// 调用后无法再次 Start。
    /// 幂等。
    /// </summary>
#pragma warning disable CA1031 // 应用关闭时的完整清理，异常安全忽略
    public void Shutdown()
    {
        _isRunning = false;
        StopTimer();

        if (_hWnd != IntPtr.Zero && _subclassDelegate != null)
        {
            try { Native.RemoveWindowSubclass(_hWnd, _subclassDelegate, 1); }
            catch { /* 忽略清理时的异常 */ }
            _subclassDelegate = null;
        }

        OnDragStopped = null;

        if (_ghostWindow != null)
        {
            try { _ghostWindow.Close(); }
            catch { /* 忽略 */ }
            _ghostWindow = null;
        }

        _hWnd = IntPtr.Zero;
        _appWindow = null;
        System.Diagnostics.Debug.WriteLine("[TopLevelGhost] shutdown complete");
    }
#pragma warning restore CA1031

    private void CreateWindow(ImageSource source)
    {
        // Image 和容器均不参与命中测试，确保鼠标穿透到下方窗口
        var image = new Image
        {
            Source = source,
            Width = _ghostWidthDip,
            Height = _ghostHeightDip,
            Stretch = Stretch.Fill,
            Opacity = 0.85,
            IsHitTestVisible = false,
        };

        // 深色背景防止白边，根容器大小匹配 DIP 尺寸
        var root = new Grid
        {
            Width = _ghostWidthDip,
            Height = _ghostHeightDip,
            Background = new SolidColorBrush(Color.FromArgb(255, 22, 22, 40)),
            IsHitTestVisible = false,
            Children = { image },
        };

        _ghostWindow = new Window
        {
            Title = "",
            Content = root,
        };

        _hWnd = WindowNative.GetWindowHandle(_ghostWindow);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // 无标题栏 + 置顶（不在此处 Resize，统一在 Show 中用 SetWindowPos 完成）
        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
        }

        // 扩展样式：工具窗口（无任务栏）、不激活
        var exStyle = Native.GetWindowLong(_hWnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hWnd, Native.GWL_EXSTYLE,
            exStyle | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);

        // WM_NCHITTEST → HTTRANSPARENT：真正的 Win32 hit-test 穿透
        // 确保拖放事件穿透 GhostWindow 到达下方 MainWindow / ChatWindow
        _subclassDelegate = OnWndProc;
        Native.SetWindowSubclass(_hWnd, _subclassDelegate, 1, 0);

        System.Diagnostics.Debug.WriteLine("[TopLevelGhost] window created, hwnd={_hWnd:X}, WM_NCHITTEST->HTTRANSPARENT");
    }

    private static IntPtr OnWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, uint dwRefData)
    {
        if (uMsg == Native.WM_NCHITTEST)
            return Native.HTTRANSPARENT;
        return Native.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void UpdateImage(ImageSource source)
    {
        if (_ghostWindow?.Content is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Image img)
            img.Source = source;
    }

    private void Show()
    {
        if (_hWnd == IntPtr.Zero) return;

        // 在窗口可见前计算初始位置，避免从 (0,0) 闪现
        Native.GetCursorPos(out var pt);
        var left = pt.X - _ghostWidthPx / 2;
        var top = pt.Y - _ghostHeightPx / 2;

        // 一次性设置位置、尺寸、topmost，然后显示（不激活）
        Native.SetWindowPos(_hWnd, Native.HWND_TOPMOST,
            left, top, _ghostWidthPx, _ghostHeightPx,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        _lastPosX = left;
        _lastPosY = top;

        System.Diagnostics.Debug.WriteLine("[TopLevelGhost] shown");
    }

    private void Hide()
    {
        if (_hWnd != IntPtr.Zero)
            Native.ShowWindow(_hWnd, Native.SW_HIDE);
    }

    private void StartTimer()
    {
        StopTimer();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }
    }

    private void OnTimerTick(object? sender, object e)
    {
        if (!_isRunning) return;

        // 检测左键释放
        if ((Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) == 0)
        {
            System.Diagnostics.Debug.WriteLine("[TopLevelGhost] left button released");
            _isRunning = false;
            StopTimer();
            Hide();
            OnDragStopped?.Invoke();
            OnDragStopped = null;
            return;
        }

        // 更新位置（仅在位置变化时调用 SetWindowPos）
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        if (_hWnd == IntPtr.Zero) return;

        Native.GetCursorPos(out var pt);

        // 居中于鼠标，使用物理像素
        var left = pt.X - _ghostWidthPx / 2;
        var top = pt.Y - _ghostHeightPx / 2;

        // 去重：位置没变就不调用 SetWindowPos
        if (left == _lastPosX && top == _lastPosY)
            return;

        _lastPosX = left;
        _lastPosY = top;

        Native.SetWindowPos(_hWnd, Native.HWND_TOPMOST,
            left, top, 0, 0,
            Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
    }

    public void Dispose()
    {
        Shutdown();
    }
}
