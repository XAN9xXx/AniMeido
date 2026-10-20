using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PendingReviewTests : DbTestBase
{
    private PersonalIdRemapService Remap() => new(DbFactory, NullLogger<PersonalIdRemapService>.Instance);
    private async Task SeedAsync(int id, string title = "来源", string at = "2026-10-20T00:00:00Z", double? rating = 8)
    {
        using var connection = await DbFactory.OpenAsync();
        await PendingReviewFixture.SeedAnimeAsync(connection, id, title, at, rating);
    }
    private async Task ExecuteAsync(string sql)
    {
        using var connection = await DbFactory.OpenAsync(); using var command = connection.CreateCommand();
        command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private async Task<object?> ScalarAsync(string sql)
    {
        using var connection = await DbFactory.OpenAsync(); using var command = connection.CreateCommand();
        command.CommandText = sql; return await command.ExecuteScalarAsync();
    }
    private async Task AddLegacyAsync(int id, string reason = "not_found", string? kept = null) => await ExecuteAsync(
        $"INSERT INTO legacy_anime_refs VALUES({-id},{id},'{reason}','来源','2026-10-20',{(kept is null ? "NULL" : "'" + kept + "'")})");

    [Fact]
    public async Task Rebind_ChangesAllFourteenTablesAndRemovesIssueAtomically()
    {
        await RunProductionMigrationAsync(); await SeedAsync(-1); await AddLegacyAsync(1);
        var service = Remap(); await service.RebindAsync(-1, 2, false);
        foreach (var table in PublicIdMigrationService.WorkTables)
        {
            Assert.Equal(1L, await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE AnimeId=2"));
            Assert.Equal(0L, await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE AnimeId=-1"));
        }
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM legacy_anime_refs"));
        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Paths.RootDirectory, "id-remap-log.json")));
        Assert.Equal(1, log.RootElement[0].GetProperty("seq").GetInt32());
        Assert.Equal(-1, log.RootElement[0].GetProperty("from").GetInt32()); Assert.Equal(2, log.RootElement[0].GetProperty("to").GetInt32());
        await AssertForeignKeysAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Merge_AppliesEveryTableRuleAndDeletesOnlyLosingPlanReminders(bool sourceNewer)
    {
        await RunProductionMigrationAsync();
        await SeedAsync(-1, "来源", sourceNewer ? "2026-10-20T00:00:00Z" : "2026-10-18T00:00:00Z");
        await SeedAsync(2, "目标", "2026-10-19T00:00:00Z", null); await AddLegacyAsync(1);
        await ExecuteAsync("""
            UPDATE tracking SET Status=5 WHERE AnimeId=2;
            UPDATE anime_progress SET CurrentEpisode=4 WHERE AnimeId=2;
            UPDATE episode_progress SET PositionSeconds=20 WHERE AnimeId=2;
            INSERT INTO episode_progress VALUES(-1,1,1,100,0,'2026-10-19');
            INSERT INTO episode_progress VALUES(2,2,2,100,0,'2026-10-19');
            INSERT INTO personal_tags(TagId,Name) VALUES(2,'来源独有'),(3,'目标独有');
            INSERT INTO anime_personal_tags VALUES(-1,2),(2,3);
            """);
        await Remap().RebindAsync(-1, 2, true);
        Assert.Equal(sourceNewer ? 2L : 5L, await ScalarAsync("SELECT Status FROM tracking WHERE AnimeId=2"));
        Assert.Equal(sourceNewer ? 3L : 4L, await ScalarAsync("SELECT CurrentEpisode FROM anime_progress WHERE AnimeId=2"));
        Assert.Equal(sourceNewer ? 10d : 20d, await ScalarAsync("SELECT PositionSeconds FROM episode_progress WHERE AnimeId=2 AND EpisodeNumber=3"));
        Assert.Equal(3L, await ScalarAsync("SELECT COUNT(*) FROM episode_progress WHERE AnimeId=2"));
        Assert.Equal(sourceNewer ? "fixture--1" : "fixture-2", await ScalarAsync("SELECT ReminderId FROM plan_reminders"));
        Assert.Equal(sourceNewer ? "来源" : "目标", await ScalarAsync("SELECT TitleSnapshot FROM anime_plans WHERE AnimeId=2"));
        Assert.Equal(4L, await ScalarAsync("SELECT ViewCount FROM browse_history WHERE AnimeId=2"));
        Assert.Equal("目标", await ScalarAsync("SELECT TitleSnapshot FROM browse_history WHERE AnimeId=2"));
        Assert.Equal(sourceNewer ? "2026-10-20T00:00:00Z" : "2026-10-19T00:00:00Z", await ScalarAsync("SELECT LastViewedAt FROM browse_history WHERE AnimeId=2"));
        Assert.Equal("目标", await ScalarAsync("SELECT TitleSnapshot FROM anime_archives WHERE AnimeId=2"));
        Assert.Equal(8d, await ScalarAsync("SELECT PersonalRating FROM anime_archives WHERE AnimeId=2"));
        Assert.Equal("目标的笔记\n——并入自〈来源〉\n来源的笔记", await ScalarAsync("SELECT SummaryNote FROM anime_archives WHERE AnimeId=2"));
        Assert.Equal(3L, await ScalarAsync("SELECT COUNT(*) FROM anime_personal_tags WHERE AnimeId=2"));
        foreach (var table in new[] { "tracking_events", "watch_sessions", "manual_watch_events", "screenshots", "archive_entries" })
            Assert.Equal(2L, await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE AnimeId=2"));
        Assert.Equal("目标", await ScalarAsync("SELECT TitleSnapshot FROM recommendation_hidden_anime WHERE AnimeId=2"));
        foreach (var table in PublicIdMigrationService.WorkTables) Assert.Equal(0L, await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE AnimeId=-1"));
        await AssertForeignKeysAsync();
    }

    [Fact]
    public async Task Merge_KeepsTargetRatingAndDoesNotDuplicateIdenticalNotes()
    {
        await RunProductionMigrationAsync(); await SeedAsync(-1); await SeedAsync(2, "目标", rating: 9); await AddLegacyAsync(1);
        await ExecuteAsync("UPDATE anime_archives SET SummaryNote='相同笔记'");
        await Remap().RebindAsync(-1, 2, true);
        Assert.Equal(9d, await ScalarAsync("SELECT PersonalRating FROM anime_archives WHERE AnimeId=2"));
        Assert.Equal("相同笔记", await ScalarAsync("SELECT SummaryNote FROM anime_archives WHERE AnimeId=2"));
    }

    [Fact]
    public async Task FailedTransaction_RollsBackRecordsIssuesCacheAndPlayerLog()
    {
        await RunProductionMigrationAsync(); await SeedAsync(-1); await SeedAsync(2, "目标"); await AddLegacyAsync(1);
        await ExecuteAsync("""
            INSERT INTO cache VALUES('fixture','cached','2099-01-01');
            CREATE TRIGGER fail_remap BEFORE UPDATE ON watch_sessions WHEN NEW.AnimeId=2
            BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;
            """);
        var before = await SnapshotAsync();
        await Assert.ThrowsAsync<SqliteException>(() => Remap().RebindAsync(-1, 2, true));
        Assert.Equal(before, await SnapshotAsync());
        Assert.False(File.Exists(Path.Combine(Paths.RootDirectory, "id-remap-log.json")));
    }

    [Fact]
    public async Task CancelOrUnconfirmedConflict_DoesNotChangeAnything_KeepAndUnkeepOnlyMark()
    {
        await RunProductionMigrationAsync(); await SeedAsync(-1); await SeedAsync(2); await AddLegacyAsync(1);
        var remap = Remap(); var pending = new PendingReviewService(DbFactory, remap);
        using var handler = new CheckHandler(); using var http = handler.Client();
        var vm = new PendingReviewViewModel(pending, remap, Metadata(http));
        await vm.LoadAsync(); var item = Assert.Single(vm.Pending); var before = await SnapshotAsync();
        Assert.False(await vm.ApplyAsync(item, 2, false, false, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => remap.RebindAsync(-1, 2, false));
        Assert.Equal(before, await SnapshotAsync()); Assert.Empty(handler.Paths);
        await vm.SetKeptAsync(item, true, default); await vm.LoadAsync();
        Assert.Empty(vm.Pending); Assert.NotNull(Assert.Single(vm.Kept).KeptAt);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=-1"));
        await vm.SetKeptAsync(item, false, default); await vm.LoadAsync();
        Assert.Single(vm.Pending); Assert.Empty(vm.Kept);
    }

    [Fact]
    public async Task NegativeCheck_AutomaticallyRestoresKeptNotFoundButNeverMerges()
    {
        await RunProductionMigrationAsync();
        for (var id = 1; id <= 4; id++) { await SeedAsync(-id); await AddLegacyAsync(id, kept: "2026-10-19"); }
        await SeedAsync(20, "已有目标");
        using var handler = new CheckHandler
        { Mapping = id => id switch { 1 => ("active", 10), 2 => ("merged", 20), 3 => ("deactivated", null), _ => ("not_found", null) } };
        using var http = handler.Client(); await using var check = Checker(http);
        await check.CheckAsync("v1");
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=10"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM legacy_anime_refs WHERE LocalId=-1"));
        Assert.Equal("conflict", await ScalarAsync("SELECT Reason FROM legacy_anime_refs WHERE LocalId=-2"));
        Assert.Equal("deactivated", await ScalarAsync("SELECT Reason FROM legacy_anime_refs WHERE LocalId=-3"));
        Assert.Equal("not_found", await ScalarAsync("SELECT Reason FROM legacy_anime_refs WHERE LocalId=-4"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=20"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=-2"));
    }

    [Fact]
    public async Task PositiveCheck_RemapWithoutConflictRegisterConflictsAndRemoveRecoveredIssues()
    {
        await RunProductionMigrationAsync(); for (var id = 1; id <= 5; id++) await SeedAsync(id, "作品" + id);
        await ExecuteAsync("INSERT INTO public_id_issues VALUES(4,'deactivated',NULL,'恢复作品','2026-10-19','2026-10-19')");
        using var handler = new CheckHandler
        {
            Work = id => id switch
            {
                1 => ("merged", 10),
                2 => ("merged", 5),
                3 => ("deactivated", null),
                _ => ("finished", null)
            }
        };
        using var http = handler.Client(); await using var check = Checker(http);
        await check.CheckAsync("v1");
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=10"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=1"));
        var items = await new PendingReviewService(DbFactory, Remap()).GetItemsAsync();
        Assert.Equal(2, items.Count); Assert.Contains(items, i => i.AnimeId == 2 && i.MergedInto == 5 && i.Reason == "merged_conflict");
        Assert.Contains(items, i => i.AnimeId == 3 && i.Reason == "deactivated");
        var wrapper = new PersonalAnimeDataSource(Metadata(http), DbFactory); var paths = handler.Paths.Count;
        var anime = await wrapper.GetAnimeDetailAsync(3, default);
        Assert.Equal("作品3", anime!.Title); Assert.Equal("资料不可用", MineBrowse.MetaText(anime)); Assert.Null(anime.CoverURL);
        Assert.Empty(await wrapper.GetTagsAsync(3, default)); Assert.Equal(paths, handler.Paths.Count);
        Assert.True((await new ArchiveService(DbFactory).GetArchiveListAsync()).Single(a => a.Archive.AnimeId == 3).Unavailable);
    }

    [Fact]
    public async Task Check_OncePerVersionButDetailRequestsBypassVersionMarker_FailureCanRetry()
    {
        await RunProductionMigrationAsync(); await SeedAsync(1);
        using var handler = new CheckHandler(); using var http = handler.Client(); await using var check = Checker(http);
        await check.CheckAsync("v1"); var count = handler.Paths.Count;
        await check.CheckAsync("v1"); Assert.Equal(count, handler.Paths.Count);
        handler.Version = "v2"; handler.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => check.CheckAsync("v2"));
        Assert.Equal("v1", await ScalarAsync("SELECT Value FROM config WHERE Key='public-id-check-version'"));
        handler.Fail = false; await check.CheckAsync("v2");
        Assert.Equal("v2", await ScalarAsync("SELECT Value FROM config WHERE Key='public-id-check-version'"));
        handler.Work = _ => ("merged", 2); await check.CheckAsync("v2", 1);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=2"));
    }

    [Fact]
    public async Task LaterBatchFailure_LeavesAllRecordsIssuesAndVersionUntouched()
    {
        await RunProductionMigrationAsync();
        for (var id = 1; id <= 101; id++) await ExecuteAsync($"INSERT INTO tracking VALUES({id},1,'2026-10-20')");
        var before = await SnapshotAsync();
        using var handler = new CheckHandler { FailOnRequest = 2, Work = id => ("merged", id + 1000) };
        using var http = handler.Client(); await using var check = Checker(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => check.CheckAsync("v1"));
        Assert.Equal(before, await SnapshotAsync());
        Assert.All(handler.Paths, p => Assert.InRange(CheckHandler.Ids(p).Length, 1, 100));
    }

    [Fact]
    public async Task V9OfflineUpgrade_BackupNotSkippedByV8Marker_AndKeptAtExportRoundTrip()
    {
        await CreateBaseTablesAsync(); using (var connection = await DbFactory.OpenAsync())
        {
            await DatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection); await DatabaseSchema.RunMigrationsAsync(connection);
            var input = await PublicIdMigrationService.ReadInputAsync(connection);
            await PublicIdMigrationService.ApplyAsync(connection, new PublicIdMigrationPlan { SourceState = input.State() });
        }
        var fakeOldBackup = Path.Combine(Paths.RootDirectory, "v8-fixture.db"); await File.WriteAllTextAsync(fakeOldBackup, "fixture");
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO config VALUES('public-id-migration-backup',@path)";
            command.Parameters.AddWithValue("@path", fakeOldBackup); await command.ExecuteNonQueryAsync();
        }
        using var handler = new CheckHandler { Fail = true }; using var http = handler.Client();
        await new DatabaseService(DbFactory, Paths).InitializeAsync();
        await new PublicIdMigrationService(DbFactory, http, NullLogger<PublicIdMigrationService>.Instance).MigrateAsync();
        Assert.Equal(10L, await ScalarAsync("PRAGMA user_version")); Assert.Empty(handler.Paths);
        Assert.Single(Directory.GetFiles(Path.Combine(Paths.BackupDirectory, "update"), "AniMeido-before-v9-to-v10-*.db"));
        await SeedAsync(-1); await AddLegacyAsync(1, kept: "2026-10-20");
        var export = new ExportService(new TrackingService(DbFactory), new SavedTagService(DbFactory), DbFactory);
        var json = await export.ExportAsync(); var data = ExportService.Preview(json)!;
        Assert.Equal("2026-10-20", Assert.Single(data.LegacyAnimeRefs!).KeptAt);
        Assert.DoesNotContain("public_id_issues", data.P4Tables!.Keys);
        await ExecuteAsync("UPDATE legacy_anime_refs SET KeptAt=NULL"); await export.ImportAsync(json);
        Assert.Equal("2026-10-20", await ScalarAsync("SELECT KeptAt FROM legacy_anime_refs"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!; node["legacyAnimeRefs"]![0]!.AsObject().Remove("keptAt");
        await export.ImportAsync(node.ToJsonString()); Assert.Equal(DBNull.Value, await ScalarAsync("SELECT KeptAt FROM legacy_anime_refs"));
    }

    [Fact]
    public async Task GenerateSyntheticPendingReviewDatabase()
    {
        var output = Environment.GetEnvironmentVariable("ANIMEIDO_C2B_FIXTURE_DIRECTORY");
        var directory = string.IsNullOrWhiteSpace(output) ? Path.Combine(Paths.RootDirectory, "pending-review-fixture") : output;
        await PendingReviewFixture.CreateAsync(directory);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "AniMeido.db")};Pooling=False");
        await connection.OpenAsync(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM legacy_anime_refs UNION ALL SELECT COUNT(*) FROM public_id_issues";
        using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(3, reader.GetInt32(0));
        Assert.True(await reader.ReadAsync()); Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(10, await DatabaseSchema.GetSchemaVersionAsync(connection));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MergedPersonalDetail_PreservesRequestedIdentityAndQueuesLocalCheck(bool targetHasRecords, bool alreadyRegistered)
    {
        await RunProductionMigrationAsync();
        await SeedAsync(1, "来源记录");
        if (targetHasRecords) await SeedAsync(2, "目标记录");
        if (alreadyRegistered)
            await ExecuteAsync("INSERT INTO public_id_issues VALUES(1,'merged_conflict',2,'来源记录','2026-10-20',NULL)");
        // 即使本版本已经完成检查，详情里的合并仍必须单独入队。
        await ExecuteAsync("INSERT INTO config VALUES('public-id-check-version','v1')");
        using var handler = new CheckHandler { Work = id => id == 1 ? ("merged", 2) : ("finished", null) };
        handler.Detail = id => id == 1
            ? handler.Reply(new { code = "merged", mergedInto = 2 }, HttpStatusCode.NotFound)
            : handler.Reply(new { data = new { id = 2, names = new[] { new { kind = "main", lang = "zh-Hans", text = "目标资料" } } } });
        using var http = handler.Client();
        var metadata = Metadata(http);
        Assert.Equal(2, (await metadata.GetAnimeDetailAsync(1, default))!.ID);
        var observed = new List<int>();
        metadata.MergedDetailObserved += observed.Add;
        var personal = new PersonalAnimeDataSource(metadata, DbFactory);
        var before = await SnapshotAsync();
        var anime = await personal.GetAnimeDetailAsync(1, default);
        Assert.Equal(1, anime!.ID);
        Assert.Equal("目标资料", anime.Title);
        Assert.Contains(1, observed); // 缓存命中也排队，不只依赖新收到的 404。
        Assert.Equal(before, await SnapshotAsync()); // 读取不替换任何个人记录 ID。

        var remap = Remap();
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remap.Changed += () => applied.TrySetResult();
        await using var check = new PublicIdCheckService(DbFactory, http, metadata, remap, NullLogger<PublicIdCheckService>.Instance);
        check.Start();
        anime = await personal.GetAnimeDetailAsync(1, default);
        Assert.Equal(1, anime!.ID);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var issues = await new PendingReviewService(DbFactory, remap).GetItemsAsync();
        if (targetHasRecords)
        {
            Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=1"));
            Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=2"));
            Assert.Equal("merged_conflict", Assert.Single(issues).Reason);
        }
        else
        {
            Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=1"));
            Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=2"));
            Assert.Empty(issues);
        }
    }

    [Fact]
    public async Task BackgroundCheck_StartsOnceAndQueuesObservedNewVersions()
    {
        await RunProductionMigrationAsync();
        await SeedAsync(1);
        using var handler = new CheckHandler();
        using var http = handler.Client();
        var metadata = Metadata(http);
        var remap = Remap();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remap.Changed += () => changed.TrySetResult();
        await using var check = new PublicIdCheckService(DbFactory, http, metadata, remap, NullLogger<PublicIdCheckService>.Instance);
        check.Start(); check.Start();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("v1", await ScalarAsync("SELECT Value FROM config WHERE Key='public-id-check-version'"));
        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Version = "v2";
        handler.Work = _ => ("deactivated", null);
        handler.Detail = id => handler.Reply(new { data = new { id } });
        await metadata.GetAnimeDetailAsync(42, default);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("v2", await ScalarAsync("SELECT Value FROM config WHERE Key='public-id-check-version'"));
        Assert.Equal("deactivated", await ScalarAsync("SELECT Kind FROM public_id_issues WHERE AnimeId=1"));
        Assert.Equal(2, handler.Paths.Count(p => p.StartsWith("/v1/works?ids=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CommittedRemap_CanRepublishPlayerLogAfterFileFailureWithoutRepeatingRewrite()
    {
        await RunProductionMigrationAsync(); await SeedAsync(-1); await AddLegacyAsync(1);
        var path = Path.Combine(Paths.RootDirectory, "id-remap-log.json");
        Directory.CreateDirectory(path);
        var remap = Remap();
        await remap.RebindAsync(-1, 2, false);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=2"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM legacy_anime_refs"));
        Directory.Delete(path);
        await remap.FlushLogAsync();
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(1, json.RootElement.GetArrayLength());
        Assert.Equal(1, json.RootElement[0].GetProperty("seq").GetInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedRemap_CompletesAndNotifiesWhenCallerCancelsBeforeLogPublication(bool failPublication)
    {
        await RunProductionMigrationAsync();
        await SeedAsync(-1);
        await AddLegacyAsync(1);
        // 改写最后写入日志时才生成缓存行，让提交后的缓存清理失败；警告回调精确触发取消。
        await ExecuteAsync("""
            CREATE TRIGGER seed_cache_after_remap_log AFTER INSERT ON config
            WHEN NEW.Key='public-id-remap-log'
            BEGIN INSERT INTO cache VALUES('cancel-after-commit','fixture','2099-01-01'); END;
            CREATE TRIGGER fail_post_commit_cache_clear BEFORE DELETE ON cache
            WHEN OLD.CacheKey='cancel-after-commit'
            BEGIN SELECT RAISE(ABORT,'synthetic cache clear failure'); END;
            """);
        using var cancellation = new CancellationTokenSource();
        var cache = new CacheService(DbFactory);
        var remap = new PersonalIdRemapService(DbFactory, new CancelOnCacheFailureLogger(cancellation), cache);
        var changed = 0;
        remap.Changed += () => changed++;
        var path = Path.Combine(Paths.RootDirectory, "id-remap-log.json");
        if (failPublication) Directory.CreateDirectory(path);

        await remap.RebindAsync(-1, 2, false, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, changed);
        foreach (var table in PublicIdMigrationService.WorkTables)
        {
            Assert.Equal(1L, await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE AnimeId=2"));
            Assert.Equal(0L, await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE AnimeId=-1"));
        }
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM legacy_anime_refs"));
        Assert.Equal(!failPublication, File.Exists(path));
        var committed = await SnapshotAsync();

        if (failPublication) Directory.Delete(path);
        await remap.FlushLogAsync();
        await remap.FlushLogAsync();

        Assert.Equal(committed, await SnapshotAsync());
        Assert.Equal(1, changed);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var entry = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(1, entry.GetProperty("seq").GetInt64());
        Assert.Equal(-1, entry.GetProperty("from").GetInt32());
        Assert.Equal(2, entry.GetProperty("to").GetInt32());
        await AssertForeignKeysAsync();
    }

    [Fact]
    public async Task Remap_CancelledBeforeCommitRollsBackAndDoesNotNotify()
    {
        await RunProductionMigrationAsync();
        await SeedAsync(-1);
        await AddLegacyAsync(1);
        using var cancellation = new CancellationTokenSource();
        var remap = Remap();
        var changed = 0;
        remap.Changed += () => changed++;
        var before = await SnapshotAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => remap.RunTransactionAsync(async (connection, tx) =>
        {
            var records = await PendingReviewService.ReadRecordsAsync(connection, tx, cancellation.Token);
            await PersonalIdRemapService.RewriteAsync(connection, tx, -1, 2, records, cancellation.Token);
            cancellation.Cancel();
        }, cancellation.Token));

        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(0, changed);
        Assert.False(File.Exists(Path.Combine(Paths.RootDirectory, "id-remap-log.json")));
    }

    [Theory]
    [InlineData("finished", "airing")]
    [InlineData("upcoming", "unknown")]
    [InlineData("future-service-status", null)]
    public async Task NormalBroadcastStatusBatch_ClearsRecoveredIssuesWithoutChangingPersonalRecords(string first, string? second)
    {
        await RunProductionMigrationAsync();
        await SeedAsync(1); await SeedAsync(2, "目标");
        await ExecuteAsync("""
            INSERT INTO public_id_issues VALUES(1,'deactivated',NULL,'恢复作品1','2026-10-19','2026-10-19');
            INSERT INTO public_id_issues VALUES(2,'merged_conflict',3,'恢复作品2','2026-10-19',NULL);
            """);
        var before = await SnapshotAsync("public_id_issues", "config");
        using var handler = new CheckHandler { Work = id => (id == 1 ? first : second, null) };
        using var http = handler.Client(); await using var check = Checker(http);
        await check.CheckAsync("v1");
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM public_id_issues"));
        Assert.Equal("v1", await ScalarAsync("SELECT Value FROM config WHERE Key='public-id-check-version'"));
        Assert.Equal(before, await SnapshotAsync("public_id_issues", "config"));
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task PositiveNotFound_LogsWarningAndPreservesRecordsIssueAndCache()
    {
        await RunProductionMigrationAsync(); await SeedAsync(1);
        await ExecuteAsync("""
            INSERT INTO public_id_issues VALUES(1,'deactivated',NULL,'已有问题','2026-10-19','2026-10-19');
            INSERT INTO cache VALUES('fixture','cached','2099-01-01');
            """);
        var before = await SnapshotAsync("config");
        using var handler = new CheckHandler { Work = _ => ("not_found", null) };
        using var http = handler.Client();
        var cache = new CacheService(DbFactory);
        var remap = new PersonalIdRemapService(DbFactory, NullLogger<PersonalIdRemapService>.Instance, cache);
        var logger = new CheckLogger();
        await using var check = new PublicIdCheckService(DbFactory, http, Metadata(http), remap, logger);
        await check.CheckAsync("v1");
        Assert.Equal(before, await SnapshotAsync("config"));
        Assert.Equal("v1", await ScalarAsync("SELECT Value FROM config WHERE Key='public-id-check-version'"));
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("not_found", warning.Message);
        Assert.Contains("1", warning.Message);
        Assert.False(File.Exists(Path.Combine(Paths.RootDirectory, "id-remap-log.json")));
    }

    [Fact]
    public async Task Check_RefreshesOccupancyAfterRewriteBeforeProcessingNextWork()
    {
        await RunProductionMigrationAsync();
        await SeedAsync(-1); await AddLegacyAsync(1); await SeedAsync(2, "另一部作品");
        using var handler = new CheckHandler { Mapping = _ => ("active", 10), Work = _ => ("merged", 10) };
        using var http = handler.Client(); await using var check = Checker(http);
        await check.CheckAsync("v1");
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM legacy_anime_refs"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=10"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=2"));
        var item = Assert.Single(await new PendingReviewService(DbFactory, Remap()).GetItemsAsync());
        Assert.Equal(2, item.AnimeId); Assert.Equal("merged_conflict", item.Reason); Assert.Equal(10, item.MergedInto);
    }

    [Fact]
    public void ItemText_GroupsTablesIntoUserCategoriesAndPicksActionByReason()
    {
        var counts = new Dictionary<string, int>
        {
            ["tracking"] = 1, ["tracking_events"] = 4, ["episode_progress"] = 3, ["archive_entries"] = 2,
            ["screenshots"] = 5, ["browse_history"] = 9, ["recommendation_hidden_anime"] = 1,
        };
        var notFound = new PendingReviewItem(-7, 7, "甲", "not_found", null, null, null, counts);
        Assert.Equal("你的记录：追番状态、观看进度、2 条感想、5 张截图", notFound.RecordsText);
        Assert.Equal("选择对应的番剧…", notFound.PrimaryActionText);
        Assert.Equal(new Uri("https://bgm.tv/subject/7"), notFound.BangumiUri);

        var merged = notFound with { AnimeId = 2, LegacyBangumiId = null, Reason = "merged_conflict", MergedInto = 5, MergedIntoTitle = "乙" };
        Assert.True(merged.HasKnownTarget);
        Assert.Equal("合并到《乙》", merged.PrimaryActionText);
        Assert.Contains("《乙》", merged.ExplanationText);
        Assert.False(merged.HasBangumiUri);

        var conflict = notFound with { Reason = "conflict" };
        Assert.True(conflict.PicksFromOwnRecords);
        Assert.False(conflict.HasKnownTarget);

        var browsedOnly = notFound with { RecordCounts = new Dictionary<string, int> { ["browse_history"] = 1 } };
        Assert.Equal("你只浏览过这部作品，没有其他记录。", browsedOnly.RecordsText);
    }

    private sealed class CancelOnCacheFailureLogger(CancellationTokenSource cancellation) : ILogger<PersonalIdRemapService>
    {
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && exception is SqliteException) cancellation.Cancel();
        }
    }

    private sealed class CheckLogger : ILogger<PublicIdCheckService>
    {
        internal ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private MetadataDataSource Metadata(HttpClient http) => new(NullLogger<MetadataDataSource>.Instance, http, new CacheService(DbFactory));
    private PublicIdCheckService Checker(HttpClient http) => new(DbFactory, http, Metadata(http), Remap(), NullLogger<PublicIdCheckService>.Instance);
    private async Task AssertForeignKeysAsync()
    {
        using var connection = await DbFactory.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_key_check";
        using var reader = await command.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
    }
    private async Task<string> SnapshotAsync(params string[] ignoredTables)
    {
        using var connection = await DbFactory.OpenAsync(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>(); using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var rows = new List<string>();
        foreach (var table in tables.Where(t => !ignoredTables.Contains(t)))
        {
            command.CommandText = $"SELECT * FROM {table}"; using var reader = await command.ExecuteReaderAsync();
            var values = new List<string>(); while (await reader.ReadAsync()) { var row = new object[reader.FieldCount]; reader.GetValues(row); values.Add(JsonSerializer.Serialize(row)); }
            rows.Add(table + ":" + string.Join('|', values.Order(StringComparer.Ordinal)));
        }
        return string.Join('\n', rows);
    }

    internal sealed class CheckHandler : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Paths { get; } = [];
        internal string Version { get; set; } = "v1";
        internal bool Fail { get; set; }
        internal int FailOnRequest { get; set; }
        internal Func<int, (string Status, int? Target)> Mapping { get; set; } = id => ("active", id + 100);
        internal Func<int, (string? Status, int? Target)> Work { get; set; } = id => (id % 2 == 0 ? "airing" : "finished", null);
        internal Func<int, HttpResponseMessage>? Detail { get; set; }
        internal HttpClient Client() => new(this, false) { BaseAddress = new Uri("https://fixture.invalid") };
        internal static int[] Ids(string path) => path.Split("ids=", StringSplitOptions.None)[1].Split(',').Select(int.Parse).ToArray();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery; Paths.Enqueue(path);
            if (Fail || Paths.Count == FailOnRequest) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (request.RequestUri.AbsolutePath.StartsWith("/v1/works/", StringComparison.Ordinal) && Detail is not null)
                return Task.FromResult(Detail(int.Parse(request.RequestUri.AbsolutePath.Split('/')[^1])));
            object body = request.Method == HttpMethod.Head ? new { items = Array.Empty<object>() } : new
            {
                items = Ids(path).Select(id =>
                {
                    if (path.Contains("mapping", StringComparison.Ordinal)) { var (status, target) = Mapping(id); return (object)new { sourceId = id.ToString(), status, publicId = target }; }
                    var work = Work(id); return new { id, status = work.Status, mergedInto = work.Target };
                }).ToList(),
            };
            return Task.FromResult(Reply(body));
        }
        internal HttpResponseMessage Reply(object body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            response.Headers.Add("X-Data-Version", Version); return response;
        }
    }
}
