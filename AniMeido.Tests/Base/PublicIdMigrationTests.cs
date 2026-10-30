using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class PublicIdMigrationTests : DbTestBase
{
    private const string At = "2026-10-19T00:00:00Z";

    [Fact]
    public async Task NewAndEmptyV8Databases_ReachCurrentWithoutNetwork()
    {
        await new DatabaseService(DbFactory, Paths).InitializeAsync();
        using var handler = new MappingHandler { RejectAll = true };
        using var client = Client(handler);
        var service = Service(client);
        await service.MigrateAsync();
        Assert.Equal(DatabaseSchema.CurrentVersion, await VersionAsync());
        Assert.Empty(handler.Paths);
        Assert.True(File.Exists(Path.Combine(Paths.RootDirectory, "id-migration-v9.json")));

        var empty = CreateAdditionalPaths();
        var factory = new SqliteConnectionFactory(empty);
        using (var connection = await factory.OpenAsync())
        {
            await DatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
            using var config = connection.CreateCommand(); config.CommandText = "CREATE TABLE config(Key TEXT PRIMARY KEY,Value TEXT NOT NULL)";
            await config.ExecuteNonQueryAsync();
            await DatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
            await DatabaseSchema.RunMigrationsAsync(connection);
        }
        await new PublicIdMigrationService(factory, client, NullLogger<PublicIdMigrationService>.Instance).MigrateAsync();
        using var check = await factory.OpenAsync();
        Assert.Equal(DatabaseSchema.CurrentVersion, await DatabaseSchema.GetSchemaVersionAsync(check));
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task ChainRewrites_AllFourteenTablesAndDeferredForeignKeysCommitTogether()
    {
        await CreateV8Async();
        await SeedAllTablesAsync(1);
        await ExecuteAsync("INSERT INTO tracking VALUES(2,1,@at)");
        using var handler = new MappingHandler { Work = id => (id + 1, "active") };
        using var client = Client(handler);
        var service = Service(client);
        await service.MigrateAsync();
        Assert.Equal(DatabaseSchema.CurrentVersion, await VersionAsync());
        foreach (var table in PublicIdMigrationService.WorkTables)
            Assert.True(await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE AnimeId=2") > 0, table);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=3"));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM cache"));
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check";
            using var reader = await command.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync());
        }
        var json = await File.ReadAllTextAsync(Path.Combine(Paths.RootDirectory, "id-migration-v9.json"));
        using var file = JsonDocument.Parse(json);
        Assert.Equal(2, file.RootElement.GetProperty("workIds").GetProperty("1").GetInt32());
        var requests = handler.Paths.Count;
        await service.MigrateAsync();
        Assert.Equal(requests, handler.Paths.Count);
    }

    [Fact]
    public async Task LaterMappingBatchFailure_BlocksAtV8WithoutChangesAndRetrySucceeds()
    {
        await CreateV8Async();
        for (var id = 1; id <= 101; id++) await ExecuteAsync($"INSERT INTO tracking VALUES({id},1,@at)");
        await ExecuteAsync("INSERT INTO cache VALUES('detail:v2:1','cached',@at)");
        var before = await SnapshotAsync();
        using var handler = new MappingHandler { FailOnMappingBatch = 2 };
        using var client = Client(handler);
        var gate = new StartupMigrationGate(() => Service(client).MigrateAsync());
        Assert.False(await gate.TryRunAsync());
        Assert.False(gate.IsReady);
        Assert.Equal(8, await VersionAsync());
        Assert.Equal(before, await SnapshotAsync());
        Assert.False(File.Exists(Path.Combine(Paths.RootDirectory, "id-migration-v9.json")));
        Assert.All(handler.Paths, path => Assert.InRange(Query(path, "ids").Split(',').Length, 1, 100));
        handler.FailOnMappingBatch = 0;
        Assert.True(await gate.TryRunAsync());
        Assert.True(gate.IsReady);
        Assert.Equal(DatabaseSchema.CurrentVersion, await VersionAsync());
        Assert.Equal(101, await ScalarAsync("SELECT COUNT(*) FROM tracking"));
    }

    [Fact]
    public async Task TransactionFailure_RollsBackEveryTableSchemaCacheAndVersion()
    {
        await CreateV8Async();
        await SeedAllTablesAsync(1);
        await ExecuteAsync("CREATE TRIGGER fail_v9 BEFORE UPDATE ON tracking WHEN NEW.AnimeId=2 BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;");
        var before = await SnapshotAsync();
        using var handler = new MappingHandler { Work = _ => (2, "active") };
        using var client = Client(handler);
        var gate = new StartupMigrationGate(() => Service(client).MigrateAsync());
        Assert.False(await gate.TryRunAsync());
        Assert.Equal(8, await VersionAsync());
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name='legacy_anime_refs'"));
        await ExecuteAsync("DROP TRIGGER fail_v9");
        Assert.True(await gate.TryRunAsync());
        Assert.Equal(DatabaseSchema.CurrentVersion, await VersionAsync());
    }

    [Theory]
    [InlineData("active", 1, 3, 1)]
    [InlineData("merged", 1, 3, 2)]
    [InlineData("merged", 1, 1, 1)]
    public async Task Conflicts_KeepActiveThenMostRecordsThenSmallerId(string firstStatus, int firstCount, int secondCount, int winner)
    {
        await CreateV8Async();
        await ExecuteAsync("INSERT INTO tracking VALUES(1,1,@at); INSERT INTO tracking VALUES(2,1,@at)");
        for (var n = 1; n < firstCount; n++) await ExecuteAsync($"INSERT INTO tracking_events VALUES('a{n}',1,NULL,1,@at)");
        for (var n = 1; n < secondCount; n++) await ExecuteAsync($"INSERT INTO tracking_events VALUES('b{n}',2,NULL,1,@at)");
        using var handler = new MappingHandler { Work = id => (50, id == 1 ? firstStatus : "merged") };
        using var client = Client(handler);
        await Service(client).MigrateAsync();
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM tracking WHERE AnimeID={-(winner == 1 ? 2 : 1)}"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeID=50"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM legacy_anime_refs WHERE Reason='conflict'"));
        Assert.Equal(2, await ScalarAsync("SELECT COUNT(*) FROM tracking"));
    }

    [Fact]
    public async Task TitleSnapshots_UseAllSourcesInPriorityOrderBeforeClearingCache()
    {
        await CreateV8Async();
        for (var id = 1; id <= 8; id++) await ExecuteAsync($"INSERT INTO tracking VALUES({id},1,@at)");
        await ExecuteAsync("""
            INSERT INTO anime_archives VALUES(1,'档案',NULL,'',@at,@at);
            INSERT INTO anime_plans VALUES(1,'低优先级',1,NULL,0,@at,@at,NULL,NULL);
            INSERT INTO anime_plans VALUES(2,'计划',1,NULL,0,@at,@at,NULL,NULL);
            INSERT INTO browse_history VALUES(2,'低优先级',@at,1);
            INSERT INTO browse_history VALUES(3,'浏览',@at,1);
            INSERT INTO manual_watch_events VALUES('m',4,'手工记录',@at,1,1,NULL,'',@at);
            INSERT INTO recommendation_hidden_anime VALUES(5,'隐藏记录',@at);
            INSERT INTO screenshots VALUES('s','fixture','not-a-real-hash',@at,'','',1,1,6,'截图标题',NULL,NULL,'');
            INSERT INTO cache VALUES('detail:v2:7','{"id":7,"title":"缓存标题"}',@at);
            INSERT INTO cache VALUES('studio:v2:8','[{"id":8,"name":"不能作为作品名的制作方"}]',@at);
            """);
        using var handler = new MappingHandler { Work = _ => (null, "not_found") };
        using var client = Client(handler);
        await Service(client).MigrateAsync();
        using var connection = await DbFactory.OpenAsync();
        using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT TitleSnapshot FROM legacy_anime_refs ORDER BY LegacyBangumiId";
        using var reader = await cmd.ExecuteReaderAsync();
        var titles = new List<string>(); while (await reader.ReadAsync()) titles.Add(reader.GetString(0));
        Assert.Equal(new[] { "档案", "计划", "浏览", "手工记录", "隐藏记录", "截图标题", "缓存标题", "未命名作品" }, titles);
    }

    [Fact]
    public async Task TagsRulesAndParticipantPreferences_ConvertAndRetainInvalidRows()
    {
        await CreateV8Async();
        await ExecuteAsync("""
            INSERT INTO saved_tags VALUES('漫画改'); INSERT INTO saved_tags VALUES('R18'); INSERT INTO saved_tags VALUES('未知');
            INSERT INTO recommendation_feature_preferences VALUES(0,'漫画改','漫画改',1,@at);
            INSERT INTO recommendation_feature_preferences VALUES(0,'未知','未知',-1,@at);
            INSERT INTO recommendation_feature_preferences VALUES(1,'1','制作方',1,@at);
            INSERT INTO recommendation_feature_preferences VALUES(1,'2','不可用制作方',-1,@at);
            INSERT INTO recommendation_feature_preferences VALUES(2,'3','不可用声优',1,@at);
            INSERT INTO smart_lists VALUES('rule','规则',1,'{"mode":"All","conditions":[{"field":"Tags","operator":"ContainsAll","value":"漫画改,未知"}]}',NULL,@at,@at);
            """);
        using var handler = new MappingHandler { Participant = id => id == 1 ? (2, "active") : (null, "not_found") };
        using var client = Client(handler);
        await Service(client).MigrateAsync();
        var states = await new SavedTagService(DbFactory).GetSavedTagStatesAsync();
        Assert.Contains(states, t => t.Name == "漫画改编" && !t.Invalid);
        Assert.Contains(states, t => t.Name == "R18" && t.Invalid);
        Assert.Contains(states, t => t.Name == "未知" && t.Invalid);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM recommendation_feature_preferences WHERE FeatureKind=1 AND FeatureKey='2' AND Invalid=0"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM recommendation_feature_preferences WHERE FeatureKey='legacy:bangumi:2' AND Invalid=1 AND Adjustment=-1"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM recommendation_feature_preferences WHERE FeatureKey='legacy:bangumi:3' AND Invalid=1 AND Adjustment=1"));
        var rules = await ReadLegacyRulesAsync(DbFactory);
        Assert.Equal(["漫画改编", "未知"], MetadataTagMigration.TagNames(rules));
        Assert.Equal(5, await ScalarAsync("SELECT COUNT(*) FROM recommendation_feature_preferences"));
        Assert.DoesNotContain(handler.Paths, p => p.Contains("mapping/work"));
        Assert.Contains(handler.Paths, p => p.Contains("mapping/participant"));
    }

    [Fact]
    public async Task NegativeIds_ResolveLocalTitleAndUnavailableStateWithoutHttp()
    {
        await CreateV8Async(); await ExecuteAsync("INSERT INTO tracking VALUES(9,1,@at)");
        using var handler = new MappingHandler { Work = _ => (null, "not_found") };
        using var client = Client(handler); await Service(client).MigrateAsync(); handler.RejectAll = true;
        var count = handler.Paths.Count;
        var remote = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, client, new CacheService(DbFactory));
        var source = new PersonalAnimeDataSource(remote, DbFactory);
        var anime = Assert.IsType<UnavailableAnime>(await source.GetAnimeDetailAsync(-9, default));
        Assert.Equal("未命名作品", anime.Title); Assert.Null(anime.CoverURL); Assert.Empty(anime.CVs);
        Assert.Equal("资料不可用", MineBrowse.MetaText(anime));
        Assert.Equal(count, handler.Paths.Count);
        Assert.Contains("资料不可用", new TodayBrowseEntry(anime, "刚刚").DisplaySubtitle);
        Assert.Contains("资料不可用", new TodayAnimeEntry(anime, "最近播放").DisplaySubtitle);
        Assert.Contains("资料不可用", new ArchiveListItem(new AnimeArchive(-9, anime.Title, null, "", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow), [], 0, 0, null).Metadata);
    }

    [Fact]
    public async Task ExportV6AndLegacyV5Import_UsePublicIdsAndCarryUnresolvedReferences()
    {
        await RunProductionMigrationAsync();
        using var handler = new MappingHandler { Work = id => id == 1 ? (10, "active") : (null, "not_found") };
        using var client = Client(handler);
        var export = Export(client);
        await export.ImportAsync(LegacyJson(1, 2));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=10"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=-2"));
        var json = await export.ExportAsync();
        var data = Assert.IsType<ExportData>(ExportService.Preview(json));
        Assert.Equal(6, data.SchemaVersion); Assert.Equal("animeido-public", data.IdSpace);
        var reference = Assert.Single(data.LegacyAnimeRefs!); Assert.Equal(2, reference.LegacyBangumiId);
        var count = handler.Paths.Count;
        await export.ImportAsync(json);
        Assert.Equal(count, handler.Paths.Count);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=-2"));
    }

    [Fact]
    public async Task LegacyImportMappingFailure_DoesNotWriteAnything()
    {
        await RunProductionMigrationAsync(); await ExecuteAsync("INSERT INTO tracking VALUES(88,1,@at)");
        var before = await SnapshotAsync();
        using var handler = new MappingHandler { FailOnMappingBatch = 2 };
        using var client = Client(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => Export(client).ImportAsync(LegacyJson(Enumerable.Range(1, 101).ToArray())));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task MappingVersionChangeOrMissingResult_RejectsWholePreparation()
    {
        await CreateV8Async(); for (var id = 1; id <= 101; id++) await ExecuteAsync($"INSERT INTO tracking VALUES({id},1,@at)");
        var before = await SnapshotAsync();
        using var handler = new MappingHandler { SwitchVersionOnSecondBatch = true };
        using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).MigrateAsync());
        Assert.Equal(before, await SnapshotAsync());
        handler.SwitchVersionOnSecondBatch = false; handler.OmitResult = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).MigrateAsync());
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task CommittedMapFileWriteFailure_RetryRecreatesFileWithoutRemappingDatabase()
    {
        await CreateV8Async(); await ExecuteAsync("INSERT INTO tracking VALUES(1,1,@at)");
        var path = Path.Combine(Paths.RootDirectory, "id-migration-v9.json"); Directory.CreateDirectory(path);
        using var handler = new MappingHandler(); using var client = Client(handler);
        var failure = await Record.ExceptionAsync(() => Service(client).MigrateAsync());
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(DatabaseSchema.CurrentVersion, await VersionAsync());
        var count = handler.Paths.Count;
        Directory.Delete(path); await Service(client).MigrateAsync();
        Assert.True(File.Exists(path)); Assert.Equal(count, handler.Paths.Count);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("rule")]
    [InlineData("preference")]
    public async Task PersonalRecordsWithoutWorkReferences_RequireOnlinePreparation(string record)
    {
        await CreateV8Async();
        await ExecuteAsync(record switch
        {
            "tag" => "INSERT INTO saved_tags VALUES('漫画改')",
            "rule" => "INSERT INTO smart_lists VALUES('r','规则',1,'{\"mode\":\"All\",\"conditions\":[]}',NULL,@at,@at)",
            _ => "INSERT INTO recommendation_feature_preferences VALUES(1,'0','失效制作方',1,@at)",
        });
        using (var offline = await DbFactory.OpenAsync())
            Assert.False(await DatabaseSchema.UpgradeEmptyDatabaseAsync(offline));
        var before = await SnapshotAsync();
        using var handler = new MappingHandler { FailAllRequests = true }; using var client = Client(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => Service(client).MigrateAsync());
        Assert.Equal(8, await VersionAsync()); Assert.Equal(before, await SnapshotAsync());
        handler.FailAllRequests = false; await Service(client).MigrateAsync();
        Assert.Equal(DatabaseSchema.CurrentVersion, await VersionAsync()); Assert.Contains(handler.Paths, p => p.Contains("/v1/vocab"));
    }

    [Fact]
    public async Task EmptyDatabaseTransactionFailure_StillAllowsBlockedWindowAndRetry()
    {
        await CreateV8Async();
        await ExecuteAsync("""
            CREATE TRIGGER fail_empty_v9 BEFORE INSERT ON config WHEN NEW.Key='public-id-migration-v9'
            BEGIN SELECT RAISE(ABORT,'synthetic empty upgrade failure'); END;
            """);
        await new DatabaseService(DbFactory, Paths).InitializeAsync();
        Assert.Equal(8, await VersionAsync());
        using var handler = new MappingHandler { RejectAll = true }; using var client = Client(handler);
        var gate = new StartupMigrationGate(() => Service(client).MigrateAsync());
        Assert.False(await gate.TryRunAsync()); Assert.Equal(8, await VersionAsync());
        await ExecuteAsync("DROP TRIGGER fail_empty_v9");
        Assert.True(await gate.TryRunAsync()); Assert.Equal(DatabaseSchema.CurrentVersion, await VersionAsync());
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task ChangedRecordsAfterPreparation_RejectTransactionBeforeChangingSchema()
    {
        await CreateV8Async(); await ExecuteAsync("INSERT INTO tracking VALUES(1,1,@at)");
        using var handler = new MappingHandler(); using var client = Client(handler);
        using var connection = await DbFactory.OpenAsync();
        var plan = await Service(client).PrepareAsync(await PublicIdMigrationService.ReadInputAsync(connection), default);
        await ExecuteAsync("INSERT INTO tracking VALUES(2,1,@at)");
        var before = await SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PublicIdMigrationService.ApplyAsync(connection, plan));
        Assert.Equal(8, await VersionAsync()); Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task PublicExport_RequiresReferencesAndPreservesRuleVocabularyOnFreshImport()
    {
        await RunProductionMigrationAsync();
        using var handler = new MappingHandler(); using var client = Client(handler);
        var legacy = JsonSerializer.Serialize(new
        {
            schemaVersion = 5, tracking = Array.Empty<object>(), savedTags = new[] { new { tagName = "漫画改" } },
            p4Tables = new Dictionary<string, object> { ["smart_lists"] = new[] { new Dictionary<string,string?>
            { ["Id"] = "rule", ["Name"] = "规则", ["SchemaVersion"] = "1", ["RuleJson"] =
                "{\"mode\":\"All\",\"conditions\":[{\"field\":\"Tags\",\"operator\":\"ContainsAll\",\"value\":\"漫画改,未知\"}]}",
                ["SortJson"] = null, ["CreatedAt"] = At, ["UpdatedAt"] = At } } },
        });
        await Export(client).ImportAsync(legacy);
        var json = await Export(client).ExportAsync();
        var exported = ExportService.Preview(json)!; Assert.Contains("漫画改编", exported.VocabularyNames!);
        var target = CreateAdditionalPaths(); var factory = new SqliteConnectionFactory(target);
        await new DatabaseService(factory, target).InitializeAsync();
        var importer = new ExportService(factory);
        await importer.ImportAsync(json);
        var rules = await ReadLegacyRulesAsync(factory);
        Assert.Equal(["漫画改编", "未知"], MetadataTagMigration.TagNames(rules));
        var roundTrip = ExportService.Preview(await importer.ExportAsync())!;
        Assert.Equal(
            exported.P4Tables!["smart_lists"].Single(),
            roundTrip.P4Tables!["smart_lists"].Single());
        exported.Tracking.Add(new TrackingEntry { AnimeId = -19, Status = AnimeTrackingStatus.Following, UpdatedAt = At });
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(JsonSerializer.Serialize(exported,
            PublicIdMigrationService.JsonOptions)));
    }

    [Fact]
    public async Task RuleVocabulary_UsesOnlyCompleteCurrentTagVocabularies()
    {
        await RunProductionMigrationAsync();
        await ExecuteAsync("""
            INSERT INTO config VALUES('metadata-tag-names','["快照标签"]');
            INSERT INTO cache VALUES('metadata:data-version','v2','2099-01-01');
            INSERT INTO cache VALUES('metadata:vocab:staff_role:data:v2','{"items":[{"nameZh":"导演"}]}','2099-01-01');
            INSERT INTO cache VALUES('metadata:vocab:tag:data:v1','{"items":[{"nameZh":"旧版本标签"}]}','2099-01-01');
            """);
        Assert.Equal(new[] { "快照标签" }, await MetadataTagAvailability.ReadNamesAsync(DbFactory, default));
        foreach (var category in new[] { "tag", "format", "country", "source_type" })
            await ExecuteAsync($$"""
                INSERT INTO cache VALUES('metadata:vocab:{{category}}:data:v2','{"items":[{"nameZh":"当前{{category}}"}]}','2099-01-01');
                """);
        var names = await MetadataTagAvailability.ReadNamesAsync(DbFactory, default);
        Assert.Equal(4, names!.Count); Assert.Contains("当前tag", names);
        Assert.DoesNotContain("快照标签", names); Assert.DoesNotContain("导演", names); Assert.DoesNotContain("旧版本标签", names);
    }

    private async Task CreateV8Async()
    {
        await CreateBaseTablesAsync();
        using var connection = await DbFactory.OpenAsync();
        await DatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
        await DatabaseSchema.RunMigrationsAsync(connection);
    }
    private async Task SeedAllTablesAsync(int id)
    {
        await ExecuteAsync($$"""
            INSERT INTO tracking VALUES({{id}},1,@at);
            INSERT INTO browse_history VALUES({{id}},'浏览',@at,1);
            INSERT INTO anime_plans VALUES({{id}},'计划',1,NULL,0,@at,@at,NULL,NULL);
            INSERT INTO plan_reminders VALUES('r',{{id}},1,NULL,NULL,NULL,@at,0,NULL,NULL);
            INSERT INTO anime_progress VALUES({{id}},3,1,2,@at);
            INSERT INTO episode_progress VALUES({{id}},3,1,2,0,@at);
            INSERT INTO watch_sessions VALUES('w',{{id}},3,1,2,0,@at);
            INSERT INTO anime_archives VALUES({{id}},'档案',NULL,'',@at,@at);
            INSERT INTO archive_entries VALUES('entry',{{id}},@at,3,'感想',@at,@at);
            INSERT INTO personal_tags VALUES(1,'个人');
            INSERT INTO anime_personal_tags VALUES({{id}},1);
            INSERT INTO screenshots VALUES('s','fixture','not-a-real-hash',@at,'','',1,1,{{id}},'截图',NULL,NULL,'');
            INSERT INTO manual_watch_events VALUES('m',{{id}},'手工',@at,1,1,NULL,'',@at);
            INSERT INTO tracking_events VALUES('t',{{id}},NULL,1,@at);
            INSERT INTO recommendation_hidden_anime VALUES({{id}},'隐藏',@at);
            INSERT INTO cache VALUES('detail:v2:{{id}}','{"id":{{id}},"title":"缓存"}',@at);
            """);
    }
    private async Task ExecuteAsync(string sql)
    {
        using var connection = await DbFactory.OpenAsync(); using var cmd = connection.CreateCommand();
        cmd.CommandText = sql; cmd.Parameters.AddWithValue("@at", At); await cmd.ExecuteNonQueryAsync();
    }
    private static async Task<string> ReadLegacyRulesAsync(SqliteConnectionFactory factory)
    {
        using var connection = await factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RuleJson FROM smart_lists WHERE Id = 'rule'";
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private async Task<int> ScalarAsync(string sql)
    {
        using var connection = await DbFactory.OpenAsync(); using var cmd = connection.CreateCommand(); cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
    private Task<int> VersionAsync() => ScalarAsync("PRAGMA user_version");
    private async Task<string> SnapshotAsync()
    {
        using var connection = await DbFactory.OpenAsync();
        using var tables = connection.CreateCommand(); tables.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='table' ORDER BY name";
        var names = new List<(string Name, string Sql)>();
        using (var reader = await tables.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add((reader.GetString(0), reader.GetString(1)));
        var result = new List<string>();
        foreach (var (name, sql) in names)
        {
            result.Add(sql);
            using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(JsonSerializer.Serialize(values));
            }
            result.AddRange(rows.Order(StringComparer.Ordinal));
        }
        return string.Join('\n', result);
    }
    private PublicIdMigrationService Service(HttpClient client) => new(DbFactory, client, NullLogger<PublicIdMigrationService>.Instance);
    private ExportService Export(HttpClient client) => new(DbFactory, Service(client));
    private static HttpClient Client(HttpMessageHandler handler) => new(handler, false) { BaseAddress = new Uri(MetadataServiceSettings.QueryBaseUrl) };
    private static string LegacyJson(params int[] ids) => JsonSerializer.Serialize(new
    {
        schemaVersion = 5, tracking = ids.Select(id => new { animeId = id, status = 1, updatedAt = At }),
    });
    private static string Query(string path, string key) => new Uri("https://fixture" + path).Query.TrimStart('?').Split('&')
        .Select(p => p.Split('=', 2)).Single(p => p[0] == key)[1];

    internal sealed class MappingHandler : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Paths { get; } = new();
        internal Func<int, (int? Id, string Status)> Work { get; set; } = id => (id + 100, "active");
        internal Func<int, (int? Id, string Status)> Participant { get; set; } = id => (id + 1000, "active");
        internal bool RejectAll { get; set; }
        internal bool FailAllRequests { get; set; }
        internal int FailOnMappingBatch { get; set; }
        internal bool SwitchVersionOnSecondBatch { get; set; }
        internal bool OmitResult { get; set; }
        private int _batch;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.False(RejectAll, "Unexpected network request");
            var path = request.RequestUri!.PathAndQuery; Paths.Enqueue(path);
            if (FailAllRequests) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            object body; var version = "20261019.2";
            if (request.RequestUri.AbsolutePath.StartsWith("/v1/mapping/", StringComparison.Ordinal))
            {
                _batch++;
                if (_batch == FailOnMappingBatch) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                if (SwitchVersionOnSecondBatch && _batch == 2) version = "20261020.1";
                var ids = Query(path, "ids").Split(',').Select(int.Parse).ToList();
                Assert.InRange(ids.Count, 1, 100); Assert.Equal("bangumi", Query(path, "source"));
                if (OmitResult) ids.RemoveAt(0);
                body = new { items = ids.Select(id =>
                {
                    var mapped = request.RequestUri.AbsolutePath.EndsWith("/work", StringComparison.Ordinal) ? Work(id) : Participant(id);
                    return new { sourceId = id.ToString(), publicId = mapped.Id, status = mapped.Status };
                }).ToList() };
            }
            else
            {
                var category = Query(path, "category");
                var (code, name) = category switch
                {
                    "tag" => ("sci-fi", "科幻"), "format" => ("tv", "TV"), "country" => ("JP", "日本"),
                    "source_type" => ("manga", "漫画改编"), _ => throw new InvalidOperationException(category),
                };
                body = new { items = new[] { new { category, code, nameZh = name } } };
            }
            var result = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            result.Headers.Add("X-Data-Version", version);
            return Task.FromResult(result);
        }
    }
}
