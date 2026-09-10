using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests
{
    public class ExportServiceTests : DbTestBase
    {
        private ExportService CreateService()
        {
            var tracking = new TrackingService(DbFactory);
            var savedTag = new SavedTagService(DbFactory);
            return new ExportService(tracking, savedTag, DbFactory);
        }

        [Fact]
        public async Task Export_EmptyDatabase_ReturnsValidJson()
        {
            await RunProductionMigrationAsync();
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
            await RunProductionMigrationAsync();
            var tracking = new TrackingService(DbFactory);
            var savedTag = new SavedTagService(DbFactory);

            // 准备数据
            await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await tracking.SetStatusAsync(2, AnimeTrackingStatus.Completed);
            await savedTag.SaveTagAsync("原创");

            // 导出
            var svc = new ExportService(tracking, savedTag, DbFactory);
            var json = await svc.ExportAsync();

            // 用新的独立数据库验证导入
            var mockImportPaths = new MockAppDataPaths();
            var importDbFactory = new SqliteConnectionFactory(mockImportPaths);
            var importDbPath = mockImportPaths.DatabasePath;
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
                        TagName TEXT NOT NULL PRIMARY KEY
                    )
                """;
                await importCmd.ExecuteNonQueryAsync();
            }

            // 导入
            var importSvc = new ExportService(
                new TrackingService(importDbFactory),
                new SavedTagService(importDbFactory),
                importDbFactory);
            var (
                trackingCount,
                configCount,
                tagCount,
                _) = await importSvc.ImportAsync(json);

            Assert.Equal(2, trackingCount);
            Assert.Equal(4, configCount); // 导出时包含默认的4个拖放配置
            Assert.Equal(1, tagCount);

            // 验证导入结果
            var status1 = await new TrackingService(importDbFactory).GetStatusAsync(1);
            var status2 = await new TrackingService(importDbFactory).GetStatusAsync(2);
            var tags = await new SavedTagService(importDbFactory).GetAllSavedTagsAsync();

            Assert.Equal(AnimeTrackingStatus.Watching, status1);
            Assert.Equal(AnimeTrackingStatus.Completed, status2);
            Assert.Contains("原创", tags);

            // 清理
            CleanupDbFile(mockImportPaths.DatabasePath);
        }

        [Fact]
        public async Task Preview_DoesNotWriteData()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();
            var json = await svc.ExportAsync();

            // 预览
            var preview = ExportService.Preview(json);
            Assert.NotNull(preview);

            // 数据应未被修改（预览不写入）
            var tracking = new TrackingService(DbFactory);
            var all = await tracking.GetAllTrackingAsync();
            Assert.Empty(all);
        }

        [Fact]
        public async Task Export_And_Import_RestoresActionCenterData()
        {
            await RunProductionMigrationAsync();
            var actionCenter = new ActionCenterService(DbFactory);
            await actionCenter.UpsertPlanAsync(
                42,
                "测试番剧",
                AniMeido.Plugin.Base.Models.AnimePlanPriority.High,
                new DateOnly(2026, 8, 1),
                0);
            await actionCenter.RecordAsync(
                new AniMeido.Contracts.Playback.AnimePlaybackProgress(
                    "export-progress",
                    42,
                    3,
                    570,
                    600,
                    false,
                    DateTimeOffset.UtcNow));
            var json = await CreateService().ExportAsync();

            var importPaths = new MockAppDataPaths();
            var importFactory = new SqliteConnectionFactory(importPaths);
            var database = new AniMeido.App.Services.DatabaseService(
                importFactory,
                importPaths);
            await database.InitializeAsync();
            var importService = new ExportService(
                new TrackingService(importFactory),
                new SavedTagService(importFactory),
                importFactory);
            await importService.ImportAsync(json);

            var imported = new ActionCenterService(importFactory);
            var plan = await imported.GetPlanAsync(42);
            var progress = await imported.GetProgressAsync();
            Assert.NotNull(plan);
            Assert.Equal("测试番剧", plan!.TitleSnapshot);
            Assert.Equal(3, progress[42].CurrentEpisode);
            CleanupDbFile(importPaths.DatabasePath);
        }

        [Fact]
        public async Task Export_And_Import_RestoresRecommendationFeedback()
        {
            await RunProductionMigrationAsync();
            await using (var connection = await DbFactory.OpenAsync())
            {
                var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO recommendation_feature_preferences(
                        FeatureKind, FeatureKey, DisplayName,
                        Adjustment, UpdatedAt)
                    VALUES(0, 'SCI-FI', '科幻', 1, '2026-08-01T00:00:00Z');
                    INSERT INTO recommendation_hidden_anime(
                        AnimeId, TitleSnapshot, HiddenAt)
                    VALUES(42, '隐藏番剧', '2026-08-01T00:00:00Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var json = await CreateService().ExportAsync();
            var importPaths = new MockAppDataPaths();
            var importFactory = new SqliteConnectionFactory(importPaths);
            await new AniMeido.App.Services.DatabaseService(
                importFactory,
                importPaths).InitializeAsync();
            await new ExportService(
                new TrackingService(importFactory),
                new SavedTagService(importFactory),
                importFactory).ImportAsync(json);

            await using var imported = await importFactory.OpenAsync();
            var check = imported.CreateCommand();
            check.CommandText = """
                SELECT DisplayName || ':' || Adjustment
                FROM recommendation_feature_preferences
                WHERE FeatureKind = 0 AND FeatureKey = 'SCI-FI'
                """;
            Assert.Equal("科幻:1", await check.ExecuteScalarAsync());
            check.CommandText = """
                SELECT TitleSnapshot FROM recommendation_hidden_anime
                WHERE AnimeId = 42
                """;
            Assert.Equal("隐藏番剧", await check.ExecuteScalarAsync());
            CleanupDbFile(importPaths.DatabasePath);
        }

        [Fact]
        public async Task Import_InvalidJson_ThrowsJsonException()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();

            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
                svc.ImportAsync("not json"));
        }

        [Fact]
        public async Task Import_EmptyTracking_DoesNotThrow()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();
            var json = await svc.ExportAsync();

            var (
                trackingCount,
                configCount,
                tagCount,
                _) = await svc.ImportAsync(json);
            Assert.Equal(0, trackingCount);
        }

        private static void CleanupDbFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { /* 忽略清理失败 */ }
        }
    }
}
