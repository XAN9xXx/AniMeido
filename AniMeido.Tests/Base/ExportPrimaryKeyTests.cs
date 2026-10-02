using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class ExportPrimaryKeyTests : DbTestBase
{
    // 固定旧 switch 的结果，不能从白名单首列生成预期值。
    private static readonly IReadOnlyDictionary<string, string[]> LegacyPrimaryKeys =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["anime_plans"] = ["AnimeId"],
            ["plan_reminders"] = ["ReminderId"],
            ["anime_progress"] = ["AnimeId"],
            ["episode_progress"] = ["AnimeId", "EpisodeNumber"],
            ["watch_sessions"] = ["EventId"],
            ["smart_lists"] = ["Id"],
            ["anime_archives"] = ["AnimeId"],
            ["archive_entries"] = ["EntryId"],
            ["personal_tags"] = ["TagId"],
            ["anime_personal_tags"] = ["AnimeId", "TagId"],
            ["manual_watch_events"] = ["EventId"],
            ["tracking_events"] = ["EventId"],
            ["recommendation_feature_preferences"] = ["FeatureKind", "FeatureKey"],
            ["recommendation_hidden_anime"] = ["AnimeId"],
        };

    [Fact]
    public async Task Import_MigratedPrimaryKeysMatchLegacyAndChangedKeyIsUsed()
    {
        await RunProductionMigrationAsync();
        await AssertMigratedPrimaryKeysAsync();

        // 新主键仍在白名单内，但不再是首列；旧 switch 无法导入。
        await ExecuteAsync("""
            DROP TABLE recommendation_hidden_anime;
            CREATE TABLE recommendation_hidden_anime(
                AnimeId INTEGER NOT NULL,
                TitleSnapshot TEXT PRIMARY KEY,
                HiddenAt TEXT NOT NULL
            );
            INSERT INTO recommendation_hidden_anime VALUES(1, '同一标题', '2026-09-01');
            """);
        var package = Package("recommendation_hidden_anime",
            new() { ["AnimeId"] = "2", ["TitleSnapshot"] = "同一标题", ["HiddenAt"] = "2026-10-03" });

        var first = await CreateService().ImportAsync(Serialize(package));
        var second = await CreateService().ImportAsync(Serialize(package));

        Assert.Equal(1, first.actionCenterCount);
        Assert.Equal(first, second);
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AnimeId || ':' || HiddenAt FROM recommendation_hidden_anime";
        Assert.Equal("2:2026-10-03", await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unlisted")]
    [InlineData("partly-unlisted")]
    public async Task Import_InvalidPrimaryKeyRollsBackEarlierWrites(string keyKind)
    {
        await RunProductionMigrationAsync();
        var keyClause = keyKind switch
        {
            "none" => "",
            "unlisted" => ", HiddenKey TEXT PRIMARY KEY DEFAULT 'fixture-key'",
            "partly-unlisted" => ", HiddenKey TEXT NOT NULL DEFAULT 'fixture-key', PRIMARY KEY(AnimeId, HiddenKey)",
            _ => throw new ArgumentOutOfRangeException(nameof(keyKind)),
        };
        // 保留 AnimeId UNIQUE，让旧 ON CONFLICT 仍合法：没有防护时会错误提交。
        await ExecuteAsync($"""
            DROP TABLE recommendation_hidden_anime;
            CREATE TABLE recommendation_hidden_anime(
                AnimeId INTEGER NOT NULL UNIQUE,
                TitleSnapshot TEXT NOT NULL,
                HiddenAt TEXT NOT NULL
                {keyClause}
            );
            INSERT INTO tracking VALUES(1, 1, '2026-09-01');
            INSERT INTO saved_tags VALUES('本地收藏');
            INSERT INTO anime_plans(AnimeId, TitleSnapshot, CreatedAt, UpdatedAt)
            VALUES(1, '本地计划', '2026-09-01', '2026-09-01');
            INSERT INTO recommendation_hidden_anime(AnimeId, TitleSnapshot, HiddenAt)
            VALUES(1, '本地隐藏', '2026-09-01');
            """);
        var before = await SnapshotAsync();
        var package = Package("recommendation_hidden_anime",
            new() { ["AnimeId"] = "1", ["TitleSnapshot"] = "导入隐藏", ["HiddenAt"] = "2026-10-03" });
        package.Tracking =
        [
            new() { AnimeId = 1, Status = AnimeTrackingStatus.Completed, UpdatedAt = "2026-10-03" },
            new() { AnimeId = 2, Status = AnimeTrackingStatus.Watching, UpdatedAt = "2026-10-03" },
        ];
        package.SavedTags = [new() { TagName = "导入收藏" }];
        package.P4Tables!["anime_plans"] =
        [
            new()
            {
                ["AnimeId"] = "1", ["TitleSnapshot"] = "导入计划", ["Priority"] = "2",
                ["SortOrder"] = "0", ["CreatedAt"] = "2026-09-01", ["UpdatedAt"] = "2026-10-03",
            },
        ];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService().ImportAsync(Serialize(package)));

        Assert.Contains("recommendation_hidden_anime", error.Message);
        Assert.Contains("主键", error.Message);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_PersonalTagMergeAlsoRejectsInvalidPrimaryKey(bool unlistedKey)
    {
        await RunProductionMigrationAsync();
        var keyClause = unlistedKey
            ? ", HiddenKey TEXT PRIMARY KEY DEFAULT 'fixture-key'"
            : "";
        await ExecuteAsync($"""
            DROP TABLE personal_tags;
            CREATE TABLE personal_tags(
                TagId INTEGER NOT NULL UNIQUE DEFAULT 1,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE
                {keyClause}
            );
            INSERT INTO tracking VALUES(1, 1, '2026-09-01');
            """);
        var before = await SnapshotAsync();
        var package = Package("personal_tags", new() { ["TagId"] = "42", ["Name"] = "导入标签" });
        package.Tracking = [new() { AnimeId = 1, Status = AnimeTrackingStatus.Completed }];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService().ImportAsync(Serialize(package)));

        Assert.Contains("personal_tags", error.Message);
        Assert.Contains("主键", error.Message);
        Assert.Equal(before, await SnapshotAsync());
    }

    private async Task AssertMigratedPrimaryKeysAsync()
    {
        var exported = ExportService.Preview(await CreateService().ExportAsync())!;
        // 导出遍历生产白名单；新表未补测试常量时，这里也会失败。
        Assert.Equal(LegacyPrimaryKeys.Keys.Order(StringComparer.Ordinal),
            exported.P4Tables!.Keys.Order(StringComparer.Ordinal));
        using var connection = await DbFactory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        foreach (var table in LegacyPrimaryKeys)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table.Key})";
            using var reader = await command.ExecuteReaderAsync();
            var columns = new SortedDictionary<int, string>();
            while (await reader.ReadAsync())
            {
                var ordinal = reader.GetInt32(5);
                if (ordinal > 0)
                    columns.Add(ordinal, reader.GetString(1));
            }
            Assert.Equal(table.Value, columns.Values.ToArray());
        }
    }

    private ExportService CreateService()
        => new(new TrackingService(DbFactory), new SavedTagService(DbFactory), DbFactory);

    private static ExportData Package(string table, Dictionary<string, string?> row) => new()
    {
        SchemaVersion = ExportService.SchemaVersion,
        P4Tables = new(StringComparer.Ordinal) { [table] = [row] },
    };

    private static string Serialize(ExportData package)
        => JsonSerializer.Serialize(package, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private async Task ExecuteAsync(string sql)
    {
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> SnapshotAsync()
    {
        using var connection = await DbFactory.OpenAsync();
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
