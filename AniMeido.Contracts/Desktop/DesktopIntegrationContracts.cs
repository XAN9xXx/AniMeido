namespace AniMeido.Contracts.Desktop;

/// <summary>插件提供的全局快捷键动作，由 App 监听输入并调用。</summary>
public interface IGlobalShortcutAction
{
    /// <summary>用于识别动作和记录日志的稳定标识，不是按键编码。</summary>
    string Id { get; }

    /// <summary>监听器用于匹配按键的平台虚拟键码。</summary>
    int VirtualKey { get; }

    /// <summary>当前是否允许触发；监听器在处理输入时读取。</summary>
    bool IsEnabled { get; }

    /// <summary>启用时是否阻止匹配按键继续传给其他应用。</summary>
    bool SuppressInput { get; }

    /// <summary>在开始监听前准备动作所需状态；令牌控制本次初始化。</summary>
    Task InitializeAsync(
        CancellationToken cancellationToken = default);

    /// <summary>执行一次已接纳的快捷键动作；令牌用于请求取消本次执行。</summary>
    /// <remarks>需要操作界面的实现自行切换到界面线程。取消不代表已产生的副作用已撤销。</remarks>
    Task ExecuteAsync(CancellationToken cancellationToken = default);
}

/// <summary>由 App 提供的前台窗口捕获能力。</summary>
public interface IForegroundWindowCaptureService
{
    /// <summary>当前平台是否支持捕获，不保证此时存在可捕获的前台窗口。</summary>
    bool IsSupported { get; }

    /// <summary>捕获调用时的前台窗口，返回 PNG 数据和窗口信息，不负责保存文件。</summary>
    /// <exception cref="NotSupportedException">当前平台不支持捕获。</exception>
    /// <exception cref="InvalidOperationException">当前没有可捕获的前台窗口或画面。</exception>
    Task<ForegroundWindowCapture> CaptureAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>一次前台窗口捕获的内存结果，不包含持久化文件路径。</summary>
/// <param name="PngData">完整的 PNG 编码数据。</param>
/// <param name="Width">画面宽度，单位为像素。</param>
/// <param name="Height">画面高度，单位为像素。</param>
public sealed record ForegroundWindowCapture(
    byte[] PngData,
    string WindowTitle,
    string ProcessName,
    int Width,
    int Height,
    DateTimeOffset CapturedAt);

/// <summary>显示并激活 App 已有的主窗口，不创建新窗口。</summary>
public interface IAppWindowActivationService
{
    /// <summary>请求显示并激活主窗口；没有已绑定窗口时无操作。</summary>
    void ActivateMainWindow();
}

/// <summary>提供给桌面选择器设置父窗口的借用原生句柄。</summary>
public interface IWindowHandleProvider
{
    /// <summary>尝试取得当前宿主窗口的句柄；不可用时返回 false，句柄为零。</summary>
    /// <remarks>调用方不拥有句柄，不得关闭它；取得句柄不会延长窗口生命周期。</remarks>
    bool TryGetWindowHandle(out nint handle);
}
