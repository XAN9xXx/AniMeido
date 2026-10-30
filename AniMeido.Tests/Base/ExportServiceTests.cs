using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests
{
    public class ExportServiceTests : DbTestBase
    {
        private ExportService CreateService()
        {
            return new ExportService(DbFactory);
        }

        [Fact]
        public async Task ExportSnapshot_KeepsRelatedTablesFromTheSameReadVersion()
        {
            await RunProductionMigrationAsync();
            var tracking = new TrackingService(DbFactory);
            await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await new SavedTagService(DbFactory).SaveTagAsync("before");
            using var connection = await DbFactory.OpenAsync();
            using var snapshot = connection.BeginTransaction(deferred: true);
            using (var pin = connection.CreateCommand())
            {
                pin.Transaction = snapshot;
                pin.CommandText = "SELECT COUNT(*) FROM tracking";
                await pin.ExecuteScalarAsync();
            }
            // 模拟另一进程的私有 SQLite 缓存连接；共享缓存会用表锁阻止此交错。
            using (var writer = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = DbFactory.DatabasePath,
                    Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
                    Pooling = false,
                }.ToString()))
            {
                await writer.OpenAsync();
                using var change = writer.CreateCommand();
                change.CommandText = """
                    UPDATE tracking SET Status=@status WHERE AnimeId=1;
                    DELETE FROM saved_tags;
                    INSERT INTO saved_tags(TagName) VALUES('after');
                    """;
                change.Parameters.AddWithValue("@status", (int)AnimeTrackingStatus.Completed);
                await change.ExecuteNonQueryAsync();
            }

            var json = await ExportService.ExportSnapshotAsync(connection, snapshot);
            using var data = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal((int)AnimeTrackingStatus.Watching,
                data.RootElement.GetProperty("tracking")[0].GetProperty("status").GetInt32());
            Assert.Equal("before", data.RootElement.GetProperty("savedTags")[0].GetProperty("tagName").GetString());
            snapshot.Dispose();
            Assert.Equal(AnimeTrackingStatus.Completed, await tracking.GetStatusAsync(1));
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

            await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await tracking.SetStatusAsync(2, AnimeTrackingStatus.Completed);
            await savedTag.SaveTagAsync("原创");

            var svc = new ExportService(DbFactory);
            var json = await svc.ExportAsync();

            // 用新的独立数据库验证导入
            var mockImportPaths = CreateAdditionalPaths();
            var importDbFactory = new SqliteConnectionFactory(mockImportPaths);
            await new AniMeido.App.Services.DatabaseService(
                importDbFactory,
                mockImportPaths).InitializeAsync();

            var importSvc = new ExportService(importDbFactory);
            var (
                trackingCount,
                configCount,
                tagCount,
                _) = await importSvc.ImportAsync(json);

            Assert.Equal(2, trackingCount);
            Assert.Equal(4, configCount); // 导出时包含默认的4个拖放配置
            Assert.Equal(1, tagCount);

            var status1 = await new TrackingService(importDbFactory).GetStatusAsync(1);
            var status2 = await new TrackingService(importDbFactory).GetStatusAsync(2);
            var tags = await new SavedTagService(importDbFactory).GetAllSavedTagsAsync();

            Assert.Equal(AnimeTrackingStatus.Watching, status1);
            Assert.Equal(AnimeTrackingStatus.Completed, status2);
            Assert.Contains("原创", tags);

        }

        [Fact]
        public async Task Preview_DoesNotWriteData()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();
            var json = await svc.ExportAsync();

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

            var importPaths = CreateAdditionalPaths();
            var importFactory = new SqliteConnectionFactory(importPaths);
            var database = new AniMeido.App.Services.DatabaseService(
                importFactory,
                importPaths);
            await database.InitializeAsync();
            var importService = new ExportService(importFactory);
            await importService.ImportAsync(json);

            var imported = new ActionCenterService(importFactory);
            var plan = await imported.GetPlanAsync(42);
            var progress = await imported.GetProgressAsync();
            Assert.NotNull(plan);
            Assert.Equal("测试番剧", plan!.TitleSnapshot);
            Assert.Equal(3, progress[42].CurrentEpisode);
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
            var importPaths = CreateAdditionalPaths();
            var importFactory = new SqliteConnectionFactory(importPaths);
            await new AniMeido.App.Services.DatabaseService(
                importFactory,
                importPaths).InitializeAsync();
            await new ExportService(importFactory).ImportAsync(json);

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

    }
}
