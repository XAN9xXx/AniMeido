using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AniMeido.Plugin.Base.Services;

/// <summary>
/// DragGhostCard 视觉定位上下文。仅用于主窗口内拖拽过程中的 GhostCard 位置计算。
///
/// == 定位 ==
/// 记录鼠标在原 AnimeCard 内的按下位置偏移，使 GhostCard 在跟随鼠标时，
/// 保持与原始拖拽点一致的相对位置，模拟"抓住卡片"的拖拽手感。
///
/// == 生命周期 ==
/// - 预捕获：AnimeCard.PointerPressed 中启动 RenderTargetBitmap 截图
/// - 设置上下文：AnimeCard.OnBodyDragStarting 中写入尺寸/封面/位置
/// - 读取：DragDropService.ShowDragVisual / UpdateDragVisualPosition
/// - 回调：GhostSnapshotSource 就绪时触发 OnSnapshotReady
/// - 清理：DragDropService.CancelStandardDrag 中清理
///
/// == 设计原则 ==
/// - 纯视觉层数据，不参与数据传递
/// - 不用于 DropZone 判断或业务逻辑
/// - 不包含 UI 控件实例
/// - 不跨插件边界
/// </summary>
public sealed class AnimeCardDragVisualContext
{
    /// <summary>当前活动的视觉定位上下文。每次拖拽开始时更新。</summary>
    public static AnimeCardDragVisualContext? Current { get; set; }

    /// <summary>
    /// 拖拽源（AnimeCard）上的 DragOver 回调。
    /// 由 AnimeCard.OnSelfDragOver 触发，DragDropService 订阅后尽早显示 GhostCard。
    /// </summary>
    public static Action<DragEventArgs, UIElement>? OnSourceDragOver { get; set; }

    /// <summary>
    /// GhostSnapshotSource 就绪后的回调。
    /// DragDropService 订阅后可在 snapshot 完成时热更新 GhostCard 图片。
    /// </summary>
    public static Action? OnSnapshotReady { get; set; }

    /// <summary>鼠标在原 AnimeCard 内的 X 偏移。</summary>
    public double PointerOffsetX { get; init; }

    /// <summary>鼠标在原 AnimeCard 内的 Y 偏移。</summary>
    public double PointerOffsetY { get; init; }

    /// <summary>原 AnimeCard 总宽度。</summary>
    public double SourceCardWidth { get; init; }

    /// <summary>原 AnimeCard 总高度。</summary>
    public double SourceCardHeight { get; init; }

    /// <summary>
    /// 源 AnimeCard 封面当前已加载的 ImageSource。
    /// GhostCard fallback 使用，避免重新从 URL 下载。
    /// </summary>
    public ImageSource? CoverImageSource { get; init; }

    /// <summary>
    /// PointerPressed 阶段用 RenderTargetBitmap 截取的 AnimeCard 完整视觉快照。
    /// GhostCard 优先使用此快照，显示"原卡片残影"效果。
    /// 异步就绪，ShowDragVisual 中先 fallback 到 CoverImageSource，回调后热更新。
    /// </summary>
    public ImageSource? GhostSnapshotSource { get; set; }

    /// <summary>主窗口 HWND，供顶层 GhostCard 检测鼠标是否在窗口内。</summary>
    public static IntPtr HostWindowHandle { get; set; }

    /// <summary>源 AnimeCard 所在屏幕的 DPI 缩放比，用于顶层窗口物理像素换算。</summary>
    public double SourceDpiScale { get; init; } = 1.0;

    /// <summary>
    /// 清理本次拖拽的上下文。不清理页面级回调（OnSourceDragOver）。
    /// 在 EndStandardDrag 中调用。
    /// </summary>
    public static void ClearCurrentDrag()
    {
        Current = null;
        // 注意：不清理 OnSourceDragOver — 它属于页面级注册，跨拖拽有效期
    }

    /// <summary>
    /// 应用关闭时完整清理所有静态状态。
    /// 包括页面级回调、窗口句柄、ImageSource 引用。
    /// </summary>
    public static void ClearAllForShutdown()
    {
        Current = null;
        OnSourceDragOver = null;
        OnSnapshotReady = null;
        HostWindowHandle = IntPtr.Zero;
    }
}
