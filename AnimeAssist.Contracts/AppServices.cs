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
    }
}
