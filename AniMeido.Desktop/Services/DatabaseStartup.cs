using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.App.Services;

internal static class DatabaseStartup
{
    // 初始化包含同步 SQLite/备份工作；完整启动任务持有它，完成前不创建主窗口。
    // 在此整体调度，不要求各个 SQL 调用或页面自行切换线程。
    public static Task InitializeAsync(IServiceProvider provider)
        => Task.Run(() => provider.GetRequiredService<DatabaseService>().InitializeAsync());
}
