using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.App.Services;

/// <summary>
/// 数据库启动逻辑。负责初始化数据库（建表、迁移、备份）。
/// </summary>
internal static class DatabaseStartup
{
    /// <summary>初始化数据库。</summary>
    public static async Task InitializeAsync(IServiceProvider provider)
    {
        var db = provider.GetRequiredService<DatabaseService>();
        await db.InitializeAsync();
    }
}
