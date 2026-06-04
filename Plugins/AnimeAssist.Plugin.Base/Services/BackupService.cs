using AniMeido.Contracts;
using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 数据库安全备份服务。
    /// 使用 SQLite 在线备份 API（BackupDatabase）确保运行时备份的一致性。
    /// 备份目录由 IAppDataPaths.BackupDirectory 提供。
    /// 保留最近 MaxBackups 个备份。
    /// </summary>
    public class BackupService
    {
        private readonly SqliteConnectionFactory _dbFactory;
        private readonly string _backupDir;
        private const int MaxBackups = 10;

        public BackupService(SqliteConnectionFactory dbFactory, IAppDataPaths paths)
        {
            _dbFactory = dbFactory;
            _backupDir = paths.BackupDirectory;
            Directory.CreateDirectory(_backupDir);
        }

        /// <summary>
        /// 执行在线备份。使用 SQLite BackupDatabase 确保一致性。
        /// </summary>
        public async Task BackupAsync()
        {
            // 备份前执行 checkpoint，确保 WAL 内容合并
            using (var checkpointCmd = await _dbFactory.OpenAsync())
            {
                using var cmd = checkpointCmd.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(FULL)";
                await cmd.ExecuteNonQueryAsync();
            }

            var suffix = Guid.NewGuid().ToString("N")[..8];
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = Path.Combine(_backupDir, $"AniMeido-{timestamp}-{suffix}.db");

            using var source = await _dbFactory.OpenAsync();
            using var dest = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
            await dest.OpenAsync();

            source.BackupDatabase(dest);

            await dest.CloseAsync();
            await source.CloseAsync();

            // 清理旧备份
            var backups = Directory.GetFiles(_backupDir, "AniMeido-*.db")
                .OrderByDescending(f => f)
                .ToList();

            foreach (var old in backups.Skip(MaxBackups))
                File.Delete(old);
        }
    }
}
