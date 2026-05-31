using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests
{
    public class ExportServiceTests : DbTestBase
    {
        private ExportService CreateService()
        {
            var tracking = new TrackingService(DbPath);
            var savedTag = new SavedTagService(DbPath);
            return new ExportService(tracking, savedTag);
        }

        [Fact]
        public async Task Export_EmptyDatabase_ReturnsValidJson()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            var json = await svc.ExportAsync();
            Assert.False(string.IsNullOrEmpty(json));

            var preview = ExportService.Preview(json);
            Assert.NotNull(preview);
            Assert.Equal(ExportService.SchemaVersion, preview!.SchemaVersion);
        }

        [Fact]
        public async Task Export_And_Import_RestoresData()
        {
            await RunFullMigrationAsync();
            var tracking = new TrackingService(DbPath);
            var savedTag = new SavedTagService(DbPath);

            // 准备数据
            await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await tracking.SetStatusAsync(2, AnimeTrackingStatus.Completed);
            await savedTag.SaveTagAsync(1, "原创");

            // 导出
            var svc = new ExportService(tracking, savedTag);
            var json = await svc.ExportAsync();

            // 用新的独立数据库验证导入
            var importDbPath = Path.Combine(Path.GetTempPath(), $"AniMeidoTest_Import_{Guid.NewGuid():N}.db");
            // 在新数据库上建表
            using (var importConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={importDbPath}"))
            {
                await importConn.OpenAsync();
                var importCmd = importConn.CreateCommand();
                importCmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS tracking(
                        AnimeID   INTEGER PRIMARY KEY,
                        Status    INTEGER NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    )
                """;
                await importCmd.ExecuteNonQueryAsync();
                importCmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS config(
                        Key   TEXT PRIMARY KEY,
                        Value TEXT NOT NULL
                    )
                """;
                await importCmd.ExecuteNonQueryAsync();
                importCmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS saved_tags(
                        AnimeId INTEGER NOT NULL,
                        TagName TEXT NOT NULL,
                        PRIMARY KEY (AnimeId, TagName)
                    )
                """;
                await importCmd.ExecuteNonQueryAsync();
            }

            // 导入
            var importSvc = new ExportService(
                new TrackingService(importDbPath),
                new SavedTagService(importDbPath));
            var (trackingCount, configCount, tagCount) = await importSvc.ImportAsync(json);

            Assert.Equal(2, trackingCount);
            Assert.Equal(4, configCount); // 导出时包含默认的4个拖放配置
            Assert.Equal(1, tagCount);

            // 验证导入结果
            var status1 = await new TrackingService(importDbPath).GetStatusAsync(1);
            var status2 = await new TrackingService(importDbPath).GetStatusAsync(2);
            var tags = await new SavedTagService(importDbPath).GetSavedTagsAsync(1);

            Assert.Equal(AnimeTrackingStatus.Watching, status1);
            Assert.Equal(AnimeTrackingStatus.Completed, status2);
            Assert.Contains("原创", tags);

            // 清理
            CleanupDbFile(importDbPath);
        }

        [Fact]
        public async Task Preview_DoesNotWriteData()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();
            var json = await svc.ExportAsync();

            // 预览
            var preview = ExportService.Preview(json);
            Assert.NotNull(preview);

            // 数据应未被修改（预览不写入）
            var tracking = new TrackingService(DbPath);
            var all = await tracking.GetAllTrackingAsync();
            Assert.Empty(all);
        }

        [Fact]
        public async Task Import_InvalidJson_ThrowsJsonException()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
                svc.ImportAsync("not json"));
        }

        [Fact]
        public async Task Import_EmptyTracking_DoesNotThrow()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();
            var json = await svc.ExportAsync();

            var (trackingCount, configCount, tagCount) = await svc.ImportAsync(json);
            Assert.Equal(0, trackingCount);
        }

        private static void CleanupDbFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { /* 忽略清理失败 */ }
        }
    }
}
