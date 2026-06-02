namespace AniMeido.Contracts
{
    /// <summary>
    /// 应用级全局服务。仅保留必须通过静态访问的成员。
    /// </summary>
    public static class AppServices
    {
        /// <summary>
        /// 主窗口引用，用于需要窗口句柄的场景（如 FileSavePicker）。
        /// 在 MainWindow 创建后由 App 层设置。
        /// </summary>
        public static object? MainWindow { get; set; }

        /// <summary>
        /// 首个页面数据加载完成信号。用于控制开屏图淡出时机。
        /// </summary>
        public static TaskCompletionSource FirstPageLoaded { get; } = new();
    }
}
