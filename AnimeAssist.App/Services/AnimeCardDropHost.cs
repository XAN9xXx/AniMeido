using AniMeido.Contracts.DragDrop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace AniMeido.App.Services;

/// <summary>
/// 主窗口级 AnimeCard 标准拖放兜底宿主。
/// 在主窗口根容器上注册 DragOver/Drop fallback。
///
/// 职责：
/// - DragOver: 识别 AnimeCardDragPayload → AcceptedOperation = Copy（防止禁止图标）
/// - Drop: 如果内部 DropZone 已处理则跳过；否则忽略（不执行业务）
///
/// 可选路由回调：当需要从根级路由 payload 到内部 DropZone 时（如 StandardDataPackage 路径），
/// 通过 <see cref="SetDropRouter"/> 注册回调函数。
/// </summary>
public sealed class AnimeCardDropHost
{
    private bool _isRegistered;
    private Func<Point, AnimeCardDragPayload, bool>? _dropRouter;

    /// <summary>
    /// 设置外部 Drop 路由回调。由 Shell 在初始化时提供，用于将 payload 路由到正确的 DropZone。
    /// 回调签名为 (dropPoint, payload) → bool（true=已处理）。
    /// </summary>
    public void SetDropRouter(Func<Point, AnimeCardDragPayload, bool>? router)
    {
        _dropRouter = router;
        System.Diagnostics.Debug.WriteLine("[AnimeCardDropHost] Drop router " + (router != null ? "set" : "cleared"));
    }

    /// <summary>
    /// 在主窗口根 UIElement 上注册 DragOver/Drop fallback。
    /// </summary>
    public void Register(UIElement rootElement)
    {
        if (_isRegistered || rootElement == null)
            return;

        rootElement.AllowDrop = true;
        rootElement.DragOver += OnRootDragOver;
        rootElement.Drop += OnRootDrop;

        _isRegistered = true;
        System.Diagnostics.Debug.WriteLine("[AnimeCardDropHost] MainWindow AnimeCardDropHost DragOver/Drop registered");
    }

    /// <summary>
    /// 注销根元素事件。
    /// </summary>
    public void Unregister(UIElement rootElement)
    {
        if (!_isRegistered || rootElement == null)
            return;

        rootElement.DragOver -= OnRootDragOver;
        rootElement.Drop -= OnRootDrop;

        _isRegistered = false;
        System.Diagnostics.Debug.WriteLine("[AnimeCardDropHost] Unregistered");
    }

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[MainWindow] AnimeCardDropHost DragOver triggered");

        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] AnimeCard payload recognized = true");
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            System.Diagnostics.Debug.WriteLine("[MainWindow] DropHost AcceptedOperation = Copy");
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] AnimeCard payload recognized = false");
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[MainWindow] DropHost Drop triggered");

        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] DropHost no valid target, ignored - no text data");
            return;
        }

        string? text;
        try
        {
            text = await e.DataView.GetTextAsync();
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] DropHost no valid target, ignored - read failed");
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] DropHost no valid target, ignored - empty text");
            return;
        }

        var payload = AnimeCardDragPayloadSerializer.Deserialize(text);
        if (payload == null)
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] DropHost no valid target, ignored - invalid payload");
            return;
        }

        // 如果有路由回调，尝试路由到内部 DropZone
        if (_dropRouter != null)
        {
            var dropPoint = e.GetPosition(sender as UIElement);
            var handled = _dropRouter(dropPoint, payload);
            if (handled)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] DropHost routed to target: AnimeId={payload.AnimeId}");
                return;
            }
        }

        System.Diagnostics.Debug.WriteLine("[MainWindow] DropHost no valid target, ignored - root fallback");
    }
}