using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class ExportImportMergeTests : DbTestBase
{
    [Fact]
    public async Task Import_MapsTagsByNameAndPreservesLocalAssociations()
    {
        await RunProductionMigrationAsync();
        var source = await CreateSourceAsync();
        await ExecuteAsync(DbFactory, """
            INSERT INTO anime_archives VALUES(1, '本地作品', 7, '', '2026-09-01', '2026-09-01');
            INSERT INTO personal_tags(TagId, Name) VALUES(1, '喜剧'), (2, 'Sci-Fi');
            INSERT INTO anime_personal_tags VALUES(1, 1);
            """);
        await ExecuteAsync(source, """
            INSERT INTO anime_archives VALUES
                (2, '导入作品', 8, '', '2026-09-01', '2026-09-01'),
                (3, '同名标签作品', 8, '', '2026-09-01', '2026-09-01');
            INSERT INTO personal_tags(TagId, Name) VALUES(1, '科幻'), (9, 'SCI-FI');
            INSERT INTO anime_personal_tags VALUES(2, 1), (3, 9);
            """);

        await CreateService(DbFactory).ImportAsync(await CreateService(source).ExportAsync());

        Assert.Equal(new[] { "1:喜剧", "2:Sci-Fi", "3:科幻" },
            await ReadStringsAsync("SELECT TagId || ':' || Name FROM personal_tags ORDER BY TagId"));
        Assert.Equal(new[] { "1:1", "2:3", "3:2" },
            await ReadStringsAsync("SELECT AnimeId || ':' || TagId FROM anime_personal_tags ORDER BY AnimeId"));
    }

    [Fact]
    public async Task Import_UpdatesArchiveAndKeepsLocalAndImportedEntries()
    {
        await RunProductionMigrationAsync();
        var source = await CreateSourceAsync();
        await ExecuteAsync(DbFactory, """
            INSERT INTO anime_archives VALUES(42, '本地标题', 7, '本地摘要', '2026-09-01', '2026-09-01');
            INSERT INTO anime_archives VALUES(99, '仅本地作品', 6, '保留', '2026-09-01', '2026-09-01');
            INSERT INTO archive_entries VALUES('A', 42, '2026-09-01', 1, '本地笔记', '2026-09-01', '2026-09-01');
            """);
        await ExecuteAsync(source, """
            INSERT INTO anime_archives VALUES(42, '导入标题', 9, '导入摘要', '2026-09-02', '2026-09-02');
            INSERT INTO archive_entries VALUES('B', 42, '2026-09-02', 2, '导入笔记', '2026-09-02', '2026-09-02');
            """);

        await CreateService(DbFactory).ImportAsync(await CreateService(source).ExportAsync());

        var archive = await new ArchiveService(DbFactory).GetArchiveAsync(42);
        Assert.NotNull(archive);
        Assert.Equal(9, archive.PersonalRating);
        Assert.Equal("导入标题", archive.TitleSnapshot);
        Assert.Equal("导入摘要", archive.SummaryNote);
        Assert.Equal(new[] { "A:本地笔记", "B:导入笔记" },
            await ReadStringsAsync("SELECT EntryId || ':' || Body FROM archive_entries ORDER BY EntryId"));
        Assert.NotNull(await new ArchiveService(DbFactory).GetArchiveAsync(99));
    }

    [Fact]
    public async Task Import_UpdatesPlanWithoutDeletingLocalReminder()
    {
        await RunProductionMigrationAsync();
        var source = await CreateSourceAsync();
        await ExecuteAsync(DbFactory, """
            INSERT INTO anime_plans(AnimeId, TitleSnapshot, Priority, CreatedAt, UpdatedAt)
            VALUES(42, '本地计划', 1, '2026-09-01', '2026-09-01');
            INSERT INTO plan_reminders(ReminderId, AnimeId, Kind, ScheduledFor)
            VALUES('local-reminder', 42, 1, '2026-10-01');
            """);
        await ExecuteAsync(source, """
            INSERT INTO anime_plans(AnimeId, TitleSnapshot, Priority, CreatedAt, UpdatedAt)
            VALUES(42, '导入计划', 3, '2026-09-02', '2026-09-02');
            """);

        await CreateService(DbFactory).ImportAsync(await CreateService(source).ExportAsync());

        Assert.Equal(new[] { "导入计划:3" },
            await ReadStringsAsync("SELECT TitleSnapshot || ':' || Priority FROM anime_plans WHERE AnimeId = 42"));
        Assert.Equal(new[] { "local-reminder" }, await ReadStringsAsync("SELECT ReminderId FROM plan_reminders"));
    }

    [Fact]
    public async Task Import_RepeatedPackageHasSameDataAndProcessedCounts()
    {
        await RunProductionMigrationAsync();
        var source = await CreateSourceAsync();
        await SeedMergeDataAsync(source);
        await ExecuteAsync(DbFactory, """
            INSERT INTO anime_archives VALUES(99, '本地作品', 6, '', '2026-09-01', '2026-09-01');
            INSERT INTO personal_tags(TagId, Name) VALUES(1, '喜剧');
            INSERT INTO anime_personal_tags VALUES(99, 1);
            """);
        var json = await CreateService(source).ExportAsync();
        var service = CreateService(DbFactory);
        var firstCounts = await service.ImportAsync(json);
        var first = await SnapshotAsync(DbFactory);

        var secondCounts = await service.ImportAsync(json);

        Assert.Equal(firstCounts, secondCounts);
        Assert.Equal(first, await SnapshotAsync(DbFactory));
    }

    [Fact]
    public async Task Import_LateConstraintFailureRollsBackEveryTable()
    {
        await RunProductionMigrationAsync();
        var source = await CreateSourceAsync();
        await SeedMergeDataAsync(source);
        await SeedMergeDataAsync(DbFactory);
        await ExecuteAsync(DbFactory, """
            UPDATE tracking SET Status = 5;
            UPDATE anime_plans SET TitleSnapshot = '本地计划';
            UPDATE anime_archives SET PersonalRating = 6;
            UPDATE archive_entries SET Body = '本地笔记';
            INSERT INTO config(Key, Value) VALUES('drag_zones', '本地配置');
            INSERT INTO saved_tags VALUES('仅本地收藏');
            """);
        var json = await CreateService(source).ExportAsync();
        var package = ExportService.Preview(json)!;
        // 白名单中倒数第二张表在 SQLite CHECK 上失败，前面各表已执行写入。
        package.P4Tables!["recommendation_feature_preferences"] =
        [
            new()
            {
                ["FeatureKind"] = "0", ["FeatureKey"] = "invalid", ["DisplayName"] = "无效",
                ["Adjustment"] = "0", ["UpdatedAt"] = "2026-09-01",
            },
        ];
        var invalid = JsonSerializer.Serialize(package, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(ExportService.Preview(invalid));
        var before = await SnapshotAsync(DbFactory);

        await Assert.ThrowsAsync<SqliteException>(() => CreateService(DbFactory).ImportAsync(invalid));

        Assert.Equal(before, await SnapshotAsync(DbFactory));
    }

    private static ExportService CreateService(SqliteConnectionFactory factory)
        => new(new TrackingService(factory), new SavedTagService(factory), factory);

    private async Task<SqliteConnectionFactory> CreateSourceAsync()
    {
        var paths = CreateAdditionalPaths();
        var factory = new SqliteConnectionFactory(paths);
        await new DatabaseService(factory, paths).InitializeAsync();
        return factory;
    }

    private static Task SeedMergeDataAsync(SqliteConnectionFactory factory)
        => ExecuteAsync(factory, """
            INSERT INTO tracking VALUES(42, 1, '2026-09-01T00:00:00Z');
            INSERT INTO saved_tags VALUES('科幻');
            INSERT INTO anime_plans(AnimeId, TitleSnapshot, CreatedAt, UpdatedAt)
            VALUES(42, '导入计划', '2026-09-01', '2026-09-01');
            INSERT INTO plan_reminders(ReminderId, AnimeId, Kind, ScheduledFor)
            VALUES('reminder', 42, 1, '2026-10-01');
            INSERT INTO anime_archives VALUES(42, '导入作品', 8, '摘要', '2026-09-01', '2026-09-01');
            INSERT INTO archive_entries VALUES('entry', 42, '2026-09-01', 1, '笔记', '2026-09-01', '2026-09-01');
            INSERT INTO personal_tags(TagId, Name) VALUES(1, '科幻');
            INSERT INTO anime_personal_tags VALUES(42, 1);
            INSERT INTO episode_progress VALUES(42, 1, 100, 1200, 0, '2026-09-01');
            INSERT INTO recommendation_feature_preferences VALUES(0, 'SCI-FI', '科幻', 1, '2026-09-01');
            """);

    private static async Task ExecuteAsync(SqliteConnectionFactory factory, string sql)
    {
        using var connection = await factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string[]> ReadStringsAsync(string sql)
    {
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static async Task<string> SnapshotAsync(SqliteConnectionFactory factory)
    {
        using var connection = await factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        var tables = new List<string>();
        using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        }
        var snapshot = new Dictionary<string, List<string?[]>>();
        foreach (var table in tables)
        {
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
            using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string?[]>();
            while (await reader.ReadAsync())
            {
                rows.Add(Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))
                    .ToArray());
            }
            snapshot[table] = rows;
        }
        return JsonSerializer.Serialize(snapshot);
    }
}
