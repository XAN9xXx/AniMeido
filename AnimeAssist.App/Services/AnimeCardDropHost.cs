using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace AniMeido.App.Services;

/// <summary>
/// 主窗口级 AnimeCard 标准拖放兜底宿主 — 拖拽系统 Shell 层组件。
///
/// == 定位 ==
/// 确保主窗口任何位置（RootGrid / MainNaviView / ContentFrame）都能接受 AnimeCard 标准拖拽，
/// 避免页面内部未覆盖的区域显示禁止图标。不处理业务逻辑，仅路由到 DragDropService。
///
/// == 拖拽系统分层 ==
/// Shell 层（AnimeCardDropHost） → 页面层（RegisterStandardDragHost / DragDropService） → Zone 层（BuildAndShowZones）
///
/// == 设计说明 ==
/// - 使用 AddHandler(handledEventsToo=true) 注册 DragOver/Drop，确保事件不被子控件拦截。
/// - 支持注册多个宿主元素（RootGrid / MainNaviView / ContentFrame）。
/// - 刻意不注册 DragLeave：多宿主间切换会触发误清理，标准拖拽状态仅由 Drop 完成或窗口关闭清理。
/// - 所有调用最终路由到 DragDropService.HandleStandardDragOver / HandleStandardDropAsync。
///
/// == 数据事实 ==
/// 拖拽数据来源为 AnimeCardDragPayload JSON（StandardDataFormats.Text）。
/// 不直接处理 payload 解析，由 DragDropService 统一处理。
/// </summary>
public sealed class AnimeCardDropHost
{
    private readonly List<UIElement> _registeredElements = new();
    private Action<DragEventArgs>? _onDragOver;
    private Func<DragEventArgs, Task>? _onDropAsync;

    /// <summary>
    /// 设置 DragOver/Drop 处理委托。由 Shell 在初始化时提供。
    /// </summary>
    public void SetHandlers(Action<DragEventArgs>? dragOver, Func<DragEventArgs, Task>? dropAsync)
    {
        _onDragOver = dragOver;
        _onDropAsync = dropAsync;
    }

    /// <summary>
    /// 在指定 UIElement 上注册 DragOver/Drop fallback。
    /// 使用 AddHandler 确保 handledEventsToo=true，不被子控件拦截。
    /// </summary>
    public void Register(UIElement element)
    {
        if (element == null || _registeredElements.Contains(element))
            return;

        element.AllowDrop = true;

        element.AddHandler(UIElement.DragOverEvent,
            new DragEventHandler(OnRootDragOver), true);
        element.AddHandler(UIElement.DropEvent,
            new DragEventHandler(OnRootDrop), true);

        _registeredElements.Add(element);
    }

    /// <summary>
    /// 从指定 UIElement 注销事件并清理。
    /// </summary>
    public void Unregister(UIElement element)
    {
        if (element == null || !_registeredElements.Contains(element))
            return;

        element.RemoveHandler(UIElement.DragOverEvent,
            new DragEventHandler(OnRootDragOver));
        element.RemoveHandler(UIElement.DropEvent,
            new DragEventHandler(OnRootDrop));

        _registeredElements.Remove(element);
    }

    /// <summary>注销所有宿主元素。</summary>
    public void UnregisterAll()
    {
        foreach (var el in _registeredElements.ToList())
            Unregister(el);
    }

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
        {
            // 先让内部处理器更新高亮
            _onDragOver?.Invoke(e);

            // 无论如何，最终强制 AcceptedOperation = Copy，防止内部逻辑误设 None
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.Handled = true;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsContentVisible = false;
        }
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[AnimeCardDropHost] Drop triggered, sender = {sender?.GetType().Name}");

        if (_onDropAsync != null)
        {
            await _onDropAsync(e);
        }
    }
}
