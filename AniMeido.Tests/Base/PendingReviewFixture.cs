using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

/// <summary>仅生成全新合成库。手动导出由显式环境变量控制，绝不覆盖现有目录或访问用户资料目录。</summary>
internal static class PendingReviewFixture
{
    internal static async Task CreateAsync(string directory)
    {
        var root = Path.GetFullPath(directory);
        foreach (var denied in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AniMeido"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AniMeido"), @"D:\AniMeidoData" })
        {
            var path = Path.GetFullPath(denied).TrimEnd(Path.DirectorySeparatorChar);
            if (root.Equals(path, StringComparison.OrdinalIgnoreCase) || root.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("合成数据只能生成到新的独立目录，不能使用真实用户数据目录。");
        }
        if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("合成数据目标必须是尚不存在的新目录。");
        Directory.CreateDirectory(root);
        var db = new SqliteConnectionFactory(new FixturePaths(root));
        using (var connection = await db.OpenAsync())
        {
            await DatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
            using var config = connection.CreateCommand(); config.CommandText = "CREATE TABLE config(Key TEXT PRIMARY KEY,Value TEXT NOT NULL)";
            await config.ExecuteNonQueryAsync();
            await DatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
            await DatabaseSchema.RunMigrationsAsync(connection);
            await DatabaseSchema.UpgradeEmptyDatabaseAsync(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO legacy_anime_refs VALUES
                    (-1900000001,1900000001,'not_found','合成：服务未收录','2026-10-20',NULL),
                    (-1900000002,1900000002,'deactivated','合成：旧条目停用（已保留）','2026-10-20','2026-10-20'),
                    (-1900000003,1900000003,'conflict','合成：旧条目冲突','2026-10-20',NULL);
                INSERT INTO public_id_issues VALUES
                    (1900000004,'merged_conflict',1900000006,'合成：合并目标已有记录','2026-10-20',NULL),
                    (1900000005,'deactivated',NULL,'合成：公共作品已停用','2026-10-20',NULL);
                """;
            await command.ExecuteNonQueryAsync();
            foreach (var (id, title) in new (int, string)[] { (-1900000001,"合成：服务未收录"), (-1900000002,"合成：旧条目停用（已保留）"),
                (-1900000003,"合成：旧条目冲突"), (1900000004,"合成：合并目标已有记录"), (1900000005,"合成：公共作品已停用"),
                (1900000006,"合成：本地合并目标") })
                await SeedAnimeAsync(connection, id, title, "2026-10-20T00:00:00Z", 7.5);
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; await command.ExecuteNonQueryAsync();
        }
        using var pooled = db.CreateConnection(); SqliteConnection.ClearPool(pooled);
    }

    internal static async Task SeedAnimeAsync(SqliteConnection connection, int id, string title, string at, double? rating)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tracking VALUES(@id,2,@at);
            INSERT INTO browse_history VALUES(@id,@title,@at,2);
            INSERT INTO anime_plans VALUES(@id,@title,1,NULL,0,@at,@at,NULL,NULL);
            INSERT INTO plan_reminders VALUES(@event,@id,1,NULL,NULL,NULL,@at,0,NULL,NULL);
            INSERT INTO anime_progress VALUES(@id,3,10,1200,@at);
            INSERT INTO episode_progress VALUES(@id,3,10,1200,0,@at);
            INSERT INTO watch_sessions VALUES(@event,@id,3,10,1200,0,@at);
            INSERT INTO anime_archives VALUES(@id,@title,@rating,@note,@at,@at);
            INSERT INTO archive_entries VALUES(@event,@id,@at,3,@note,@at,@at);
            INSERT OR IGNORE INTO personal_tags(TagId,Name) VALUES(1,'合成标签');
            INSERT INTO anime_personal_tags VALUES(@id,1);
            INSERT INTO screenshots VALUES(@event,'fixture-not-a-real-file.png','fixture-no-file',@at,'合成窗口','fixture',1,1,@id,@title,3,10,'仅元数据，无图片文件');
            INSERT INTO manual_watch_events VALUES(@event,@id,@title,@at,1,3,20,@note,@at);
            INSERT INTO tracking_events VALUES(@event,@id,NULL,2,@at);
            INSERT INTO recommendation_hidden_anime VALUES(@id,@title,@at);
            """;
        command.Parameters.AddWithValue("@id", id); command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@at", at); command.Parameters.AddWithValue("@rating", rating ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@note", title + "的笔记"); command.Parameters.AddWithValue("@event", "fixture-" + id);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixturePaths(string root) : IAppDataPaths
    {
        public string DatabasePath => Path.Combine(root, "AniMeido.db");
        public string BackupDirectory => Path.Combine(root, "Backups");
        public string LogDirectory => Path.Combine(root, "logs");
    }
}
