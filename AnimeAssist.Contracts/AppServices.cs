namespace AniMeido.Contracts
{
    public static class AppServices
    {
        // TODO: 待重构的反模式
        /// <summary>
        /// 服务定位器
        /// </summary>
        public static IServiceProvider? Provider { get; set; }

        /// <summary>
        /// 主窗口引用，用于需要窗口句柄的场景（如 FileSavePicker）。
        /// </summary>
        public static object? MainWindow { get; set; }

        /// <summary>
        /// 数据库文件路径
        /// </summary>
        public static string? DatabasePath { get; set; }

        /// <summary>
        /// 数据库备份目录路径
        /// </summary>
        public static string? BackupDirectory { get; set; }

        /// <summary>
        /// 日志目录路径
        /// </summary>
        public static string? LogDirectory { get; set; }

        /// <summary>
        /// 触发数据库备份。由 App 层在启动时注册。
        /// </summary>
        public static Func<Task>? BackupDatabaseAsync { get; set; }

        /// <summary>
        /// 首个页面数据加载完成信号。用于控制开屏图淡出时机。
        /// </summary>
        public static TaskCompletionSource FirstPageLoaded { get; } = new();
    }
}
