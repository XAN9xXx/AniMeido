using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

/// <summary>每个测试实例独占的个人数据库；复用生产结构，并提供真实 SQLite 锁等待。</summary>
internal sealed class PersonalDatabaseFixture : IAsyncLifetime
{
    private readonly MockAppDataPaths _paths = new();

    internal SqliteConnectionFactory Factory { get; }

    public PersonalDatabaseFixture()
    {
        Factory = new SqliteConnectionFactory(_paths);
    }

    public async Task InitializeAsync()
    {
        await using var connection = await Factory.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL";
            Assert.Equal("wal", await command.ExecuteScalarAsync());
        }

        await PersonalDatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
        await using (var command = connection.CreateCommand())
        {
            // config 属于 App；业务表和迁移继续复用 Core 的真实入口。
            command.CommandText = "CREATE TABLE IF NOT EXISTS config(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync();
        }
        await PersonalDatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
        await PersonalDatabaseSchema.RunMigrationsAsync(connection);
        await PersonalDatabaseSchema.UpgradeEmptyDatabaseAsync(connection);
    }

    internal async Task<SqliteConnection> LockExclusiveAsync()
    {
        // 初始化启用 WAL 后，池中的原生连接仍持有此临时库；先关闭本库的工厂池才能切换模式。
        using (var pooledConnection = Factory.CreateConnection())
        {
            SqliteConnection.ClearPool(pooledConnection);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Factory.DatabasePath,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 2;
            // WAL 允许读者绕过写锁；此夹具明确使用文件独占锁，覆盖真实 busy 等待。
            command.CommandText = "PRAGMA journal_mode=DELETE;";
            Assert.Equal("delete", await command.ExecuteScalarAsync());
            command.CommandText = "BEGIN EXCLUSIVE;";
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public Task DisposeAsync()
    {
        _paths.Dispose();
        return Task.CompletedTask;
    }
}
