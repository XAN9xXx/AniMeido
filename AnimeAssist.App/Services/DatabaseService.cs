using Microsoft.Data.Sqlite;

namespace AniMeido.App.Services
{
    /// <summary>
    /// 数据库服务：初始化、自动备份、版本迁移、损坏检测。
    /// 数据路径: %AppData%/AniMeido/AniMeido.db
    /// 备份路径: %AppData%/AniMeido/Backups/
    /// 日志路径: %AppData%/AniMeido/logs/
    /// </summary>
    public class DatabaseService
    {
        private readonly string _connectionString;
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AniMeido");

        /// <summary>数据库文件路径</summary>
        public string DbPath { get; }

        /// <summary>日志目录路径</summary>
        public string LogDir { get; }

        /// <summary>备份目录路径</summary>
        public string BackupDir { get; }

        /// <summary>最大备份保留数</summary>
        private const int MaxBackups = 10;



        public DatabaseService()
        {
            DbPath = Path.Combine(AppDataDir, "AniMeido.db");
            LogDir = Path.Combine(AppDataDir, "logs");
            BackupDir = Path.Combine(AppDataDir, "Backups");
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(BackupDir);

            _connectionString = $"Data Source={DbPath}";
        }


        /// <summary>
        /// 初始化数据库：建表、迁移、自动备份。
        /// 如果数据库损坏，会尝试从最近的备份恢复。
        /// </summary>
        public async Task InitializeAsync()
        {
            // 首先尝试打开/创建数据库
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                await CreateTablesAsync(connection);
                await RunMigrationsAsync(connection);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 11) // SQLITE_CORRUPT
            {
                // 数据库损坏 — 尝试从备份恢复
                var restored = await TryRestoreFromBackupAsync();
                if (!restored)
                    throw new InvalidOperationException(
                        "数据库文件已损坏，且没有可用备份。请手动删除数据库文件后重启应用。", ex);

                return; // 恢复成功，初始化已完成
            }

            // 启动时自动备份（异步，不阻塞启动）
            _ = BackupAsync();
        }


        /// <summary>
        /// 创建数据库备份。保留最近 MaxBackups 个备份，超过则清理最旧的。
        /// </summary>
        public async Task BackupAsync()
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var backupPath = Path.Combine(BackupDir, $"AniMeido-{timestamp}.db");

                // SQLite 在线备份
                using var source = new SqliteConnection(_connectionString);
                await source.OpenAsync();
                using var dest = new SqliteConnection($"Data Source={backupPath}");
                await dest.OpenAsync();
                source.BackupDatabase(dest);

                // 清理旧备份
                var backups = Directory.GetFiles(BackupDir, "AniMeido-*.db")
                    .OrderByDescending(f => f)
                    .ToList();
                while (backups.Count > MaxBackups)
                {
                    try { File.Delete(backups.Last()); } catch { }
                    backups.RemoveAt(backups.Count - 1);
                }
            }
            catch
            {
                // 备份失败不影响主流程
            }
        }


        /// <summary>
        /// 尝试从最近的备份恢复数据库。
        /// </summary>
        public async Task<bool> TryRestoreFromBackupAsync()
        {
            var backups = Directory.GetFiles(BackupDir, "AniMeido-*.db")
                .OrderByDescending(f => f)
                .ToList();

            foreach (var backup in backups)
            {
                try
                {
                    // 验证备份文件完整性
                    using var test = new SqliteConnection($"Data Source={backup}");
                    await test.OpenAsync();
                    var cmd = test.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
                    await cmd.ExecuteScalarAsync();
                    test.Close();

                    // 复制备份覆盖原数据库
                    File.Copy(backup, DbPath, overwrite: true);

                    // 重新初始化
                    using var connection = new SqliteConnection(_connectionString);
                    await connection.OpenAsync();
                    await CreateTablesAsync(connection);
                    await RunMigrationsAsync(connection);
                    return true;
                }
                catch
                {
                    // 此备份也损坏，尝试下一个
                    try { File.Delete(backup); } catch { }
                }
            }
            return false;
        }


        // ======== 私有辅助 ========

        private async Task CreateTablesAsync(SqliteConnection connection)
        {
            var cmd = connection.CreateCommand();

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

            // browse_history 表预留，后续需要时启用
            /*
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS browse_history(
                    AnimeID       INTEGER PRIMARY KEY,
                    TitleSnapshot TEXT,
                    LastViewedAt  TEXT NOT NULL,
                    ViewCount     INTEGER NOT NULL DEFAULT 1
                )
            """;
            await cmd.ExecuteNonQueryAsync();
            */
        }

        private async Task RunMigrationsAsync(SqliteConnection connection)
        {
            var cmd = connection.CreateCommand();

            // 读取当前版本
            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());

            if (version < 1)
            {
                // v1: 初始版本 — 表已在 CreateTablesAsync 中创建
                cmd.CommandText = "PRAGMA user_version = 1";
                await cmd.ExecuteNonQueryAsync();
                version = 1;
            }

            // 未来迁移示例：
            // if (version < 2) { ... PRAGMA user_version = 2; }
            // if (version < 3) { ... PRAGMA user_version = 3; }
            if (version < 2)
            {
                // v2: Bangumi Tag 收藏
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS saved_tags(
                        AnimeId INTEGER NOT NULL,
                        TagName TEXT NOT NULL,
                        PRIMARY KEY (AnimeId, TagName)
                    )
                """;
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "PRAGMA user_version = 2";
                await cmd.ExecuteNonQueryAsync();
                version = 2;
            }
        }
    }
}