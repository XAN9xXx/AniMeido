using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services;

/// <summary>
/// 旧启动器直接读取此 DLL 中的常量元数据，不能用类型转发替代。
/// 版本值与迁移实现统一由 PersonalDatabaseSchema 提供。
/// </summary>
public static class DatabaseSchema
{
    public const int CurrentVersion = PersonalDatabaseSchema.CurrentVersion;
    public const int PublicIdVersion = PersonalDatabaseSchema.PublicIdVersion;
    public const int OfflineVersion = PersonalDatabaseSchema.OfflineVersion;

    public static Task<bool> UpgradeEmptyDatabaseAsync(SqliteConnection connection)
        => PersonalDatabaseSchema.UpgradeEmptyDatabaseAsync(connection);

    public static Task CreateInitialBusinessTablesBeforeConfigAsync(SqliteConnection connection)
        => PersonalDatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);

    public static Task CreateInitialBusinessTablesAfterConfigAsync(SqliteConnection connection)
        => PersonalDatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);

    public static Task RunMigrationsAsync(SqliteConnection connection)
        => PersonalDatabaseSchema.RunMigrationsAsync(connection);

    public static Task<int> GetSchemaVersionAsync(SqliteConnection connection)
        => PersonalDatabaseSchema.GetSchemaVersionAsync(connection);
}
