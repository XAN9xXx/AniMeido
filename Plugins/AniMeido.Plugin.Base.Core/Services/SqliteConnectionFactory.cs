using AniMeido.Contracts;
using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services;

/// <summary>
/// SQLite 连接工厂。集中创建连接，统一设置 PRAGMA。
/// 使用 SqliteConnectionStringBuilder 避免字符串插值带来的路径特殊字符问题。
/// 所有需要访问数据库的服务应依赖此工厂而非自行拼连接串。
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public string DatabasePath { get; }
    internal PersonalDataAccess Access { get; } = new();

    public SqliteConnectionFactory(IAppDataPaths paths)
    {
        DatabasePath = paths.DatabasePath;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        _connectionString = builder.ToString();
    }

    /// <summary>
    /// 永久停止接纳新操作，等待已接纳的完整操作和打开的连接结束。
    /// 退出所有者必须在自身未持有业务资格时等待；不会取消已接纳的写入。
    /// </summary>
    public Task StopAsync() => Access.StopAsync();

    /// <summary>
    /// 创建并打开一个 SQLite 连接，自动设置 busy_timeout。
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var lease = Access.OpenConnection();
        var connection = new SqliteConnection(_connectionString);
        connection.StateChange += (_, change) =>
        {
            if (change.CurrentState == System.Data.ConnectionState.Closed)
                lease.Dispose();
        };
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 创建一个不自动打开的 SQLite 连接（用于 BackupDatabase 等场景）。
    /// 调用方需自行 OpenAsync。
    /// </summary>
    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }
}
