namespace AniMeido.Contracts
{
    /// <summary>
    /// 应用级全局服务。仅保留必须通过静态访问的成员。
    /// MainWindow 引用用于需要窗口句柄的场景（如 FileSavePicker）。
    /// </summary>
    public static class AppServices
    {
        /// <summary>
        /// 主窗口引用，用于需要窗口句柄的场景（如 FileSavePicker）。
        /// 在 MainWindow 创建后由 App 层设置。
        /// </summary>
        public static object? MainWindow { get; set; }

        /// <summary>
        /// 应用关闭通知。各模块通过此事件在 ServiceProvider 释放前执行清理。
        /// 由 App 层在 MainWindow.Closed 事件中触发。
        /// </summary>
        public static event Action? Closing;

        /// <summary>
        /// 外部拖拽完成通知（跨插件边界，如 ChatWindow 成功接收 AnimeCard）。
        /// 主窗口 DragDropService 订阅此事件以清理 DropZone overlay。
        /// </summary>
        public static event Action? DragDropCompleted;

        /// <summary>触发外部拖拽完成通知。</summary>
        public static void NotifyDragDropCompleted() => DragDropCompleted?.Invoke();

        /// <summary>触发关闭通知。</summary>
        public static void NotifyClosing() => Closing?.Invoke();
    }
}
