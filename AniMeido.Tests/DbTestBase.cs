using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests
{
    /// <summary>
    /// 测试基类：为每个测试创建独立临时目录和 IAppDataPaths。
    /// 结束时逐库释放连接池，再清理本夹具拥有的全部根目录；短暂占用会重试，最终失败记入 TEMP 日志。
    /// </summary>
    public abstract class DbTestBase : IDisposable
    {
        protected readonly MockAppDataPaths Paths;
        protected readonly string DbPath;
        protected readonly SqliteConnectionFactory DbFactory;
        protected readonly string ConnectionString;
        private readonly List<MockAppDataPaths> _additionalPaths = new();

        protected DbTestBase()
        {
            Paths = new MockAppDataPaths();
            DbPath = Paths.DatabasePath;
            DbFactory = new SqliteConnectionFactory(Paths);
            ConnectionString = $"Data Source={DbPath}";
        }

        /// <summary>登记本测试创建的附加路径，断言失败时也由夹具统一清理。</summary>
        protected MockAppDataPaths CreateAdditionalPaths(string? dbPath = null)
        {
            var paths = new MockAppDataPaths(dbPath);
            _additionalPaths.Add(paths);
            return paths;
        }

        /// <summary>初始化三张基础表（不含 migration）。</summary>
        protected async Task CreateBaseTablesAsync()
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();

            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS tracking(
                    AnimeID   INTEGER PRIMARY KEY,
                    Status    INTEGER NOT NULL,
                    UpdatedAt TEXT NOT NULL
                )
            """;
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS cache(
                    CacheKey  TEXT PRIMARY KEY,
                    Data      TEXT NOT NULL,
                    ExpiresAt TEXT NOT NULL
                )
            """;
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS config(
                    Key   TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                )
            """;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>使用生产 DatabaseService 执行完整迁移。</summary>
        protected async Task RunProductionMigrationAsync()
        {
            var db = new AniMeido.App.Services.DatabaseService(DbFactory, Paths);
            await db.InitializeAsync();
        }

        /// <summary>
        /// 仅创建基础测试表（不含 migration 版本管理）。
        /// 注意：新测试应优先使用 RunProductionMigrationAsync。
        /// </summary>
        [Obsolete("请优先使用 RunProductionMigrationAsync 调用生产 DatabaseService")]
        protected async Task RunFullMigrationAsync()
        {
            await RunProductionMigrationAsync();
        }

        /// <summary>模拟数据库损坏（写入无效数据）。</summary>
        protected void CorruptDatabase()
        {
            File.WriteAllBytes(DbPath, new byte[] { 0x00, 0x00, 0x00, 0x00 });
        }

        public void Dispose()
        {
            foreach (var paths in _additionalPaths) paths.Dispose();
            Paths.Dispose();
        }
    }
}
