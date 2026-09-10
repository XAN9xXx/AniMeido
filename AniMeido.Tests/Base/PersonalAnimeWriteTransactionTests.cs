using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class PersonalAnimeWriteTransactionTests : DbTestBase
{
    [Fact]
    public async Task TrackingTransactionHelpers_PreserveChangedOnlyAndImportPolicies()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);

        // Normal writes record only a real state change.
        await tracking.SetStatusAsync(70, AnimeTrackingStatus.Watching);
        await tracking.SetStatusAsync(70, AnimeTrackingStatus.Watching);

        // Import writes the supplied state without an event.
        await tracking.SetStatusWithTimestampAsync(
            70,
            AnimeTrackingStatus.Completed,
            "2026-01-01T00:00:00.0000000Z");
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM tracking_events WHERE AnimeId = 70"));
        Assert.Equal(
            "2026-01-01T00:00:00.0000000Z",
            await ScalarAsync(
                "SELECT UpdatedAt FROM tracking WHERE AnimeId = 70"));

        var gateway = new PersonalAnimeDataGateway(
            DbFactory,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new NoOpNotificationService());
        var gatewayChange = new PersonalAnimeChange(
            "gateway-event-1",
            PersonalAnimeChangeKind.SetTrackingStatus,
            70,
            "事件策略测试",
            "保持状态",
            TrackingStatus: AnimeTrackingStatus.Completed);
        var first = await gateway.ApplyChangesAsync(
            new PersonalAnimeChangeSet("test-source", [gatewayChange]));
        var second = await gateway.ApplyChangesAsync(
            new PersonalAnimeChangeSet("test-source", [gatewayChange]));
        var sameStateDifferentId = gatewayChange with
        {
            ChangeId = "gateway-event-2",
        };
        var sameStateResult = await gateway.ApplyChangesAsync(
            new PersonalAnimeChangeSet("test-source", [sameStateDifferentId]));
        var changedState = gatewayChange with
        {
            ChangeId = "gateway-event-3",
            TrackingStatus = AnimeTrackingStatus.Watching,
        };
        var changedStateResult = await gateway.ApplyChangesAsync(
            new PersonalAnimeChangeSet("test-source", [changedState]));

        Assert.True(first.Results.Single().Applied);
        Assert.False(first.Results.Single().WasAlreadyApplied);
        Assert.True(second.Results.Single().WasAlreadyApplied);
        Assert.True(sameStateResult.Results.Single().Applied);
        Assert.False(sameStateResult.Results.Single().WasAlreadyApplied);
        Assert.True(changedStateResult.Results.Single().Applied);
        Assert.Equal(2, await CountAsync(
            "SELECT COUNT(*) FROM tracking_events WHERE AnimeId = 70"));
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM external_change_receipts "
            + "WHERE ChangeId = 'gateway-event-1'"));
        Assert.Equal(0, await CountAsync(
            "SELECT COUNT(*) FROM tracking_events "
            + "WHERE AnimeId = 70 AND EventId = 'gateway-event-2'"));
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM external_change_receipts "
            + "WHERE ChangeId = 'gateway-event-2'"));
        await AssertTrackingEventAsync(
            "gateway-event-3",
            AnimeTrackingStatus.Completed,
            AnimeTrackingStatus.Watching);
    }

    [Fact]
    public async Task TransactionHelpers_PreserveGatewaySortOrderAndRating()
    {
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        var archive = new ArchiveService(DbFactory);

        await actionCenter.UpsertPlanAsync(
            71,
            "原计划",
            AnimePlanPriority.Normal,
            null,
            9);
        await archive.UpsertArchiveAsync(72, "原档案", 8.5, "旧摘要");

        await using (var connection = await DbFactory.OpenAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ActionCenterService.UpsertPlanInTransactionAsync(
                connection,
                (SqliteTransaction)transaction,
                71,
                "导入计划",
                AnimePlanPriority.High,
                null,
                0,
                preserveSortOrder: true,
                now: DateTimeOffset.UtcNow.ToString("O"),
                cancellationToken: CancellationToken.None);
            await ArchiveService.UpsertArchiveInTransactionAsync(
                connection,
                (SqliteTransaction)transaction,
                72,
                "导入档案",
                rating: null,
                summary: "新摘要",
                now: DateTimeOffset.UtcNow.ToString("O"),
                preserveRating: true,
                preserveSummary: false,
                cancellationToken: CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.Equal(9, (await actionCenter.GetPlanAsync(71))!.SortOrder);
        var updatedArchive = await archive.GetArchiveAsync(72);
        Assert.Equal(8.5, updatedArchive!.PersonalRating);
        Assert.Equal("新摘要", updatedArchive.SummaryNote);
    }

    [Fact]
    public async Task GatewayAppend_PreservesArchiveFieldsAndDeduplicatesPayload()
    {
        await RunProductionMigrationAsync();
        var archive = new ArchiveService(DbFactory);
        await archive.UpsertArchiveAsync(74, "原档案", 6.5, "保留摘要");
        var gateway = new PersonalAnimeDataGateway(
            DbFactory,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new NoOpNotificationService());
        var change = new PersonalAnimeChange(
            "gateway-append-1",
            PersonalAnimeChangeKind.AppendArchiveEntry,
            74,
            "导入标题",
            "追加感想",
            Text: "第一条",
            EpisodeNumber: 3,
            OccurredAt: DateTimeOffset.Parse("2026-02-03T04:05:06Z"));

        var first = await gateway.ApplyChangesAsync(
            new PersonalAnimeChangeSet("test-source", [change]));
        var second = await gateway.ApplyChangesAsync(
            new PersonalAnimeChangeSet("test-source", [change]));
        var conflicting = await gateway.ApplyChangesAsync(
            new PersonalAnimeChangeSet(
                "test-source",
                [change with { Text = "不同载荷" }]));

        var restored = await archive.GetArchiveAsync(74);
        var entry = Assert.Single(await archive.GetEntriesAsync(74));
        Assert.True(first.Results.Single().Applied);
        Assert.True(second.Results.Single().WasAlreadyApplied);
        Assert.False(conflicting.Results.Single().Applied);
        Assert.Equal("导入标题", restored!.TitleSnapshot);
        Assert.Equal(6.5, restored.PersonalRating);
        Assert.Equal("保留摘要", restored.SummaryNote);
        Assert.Equal("gateway-append-1", entry.EntryId);
        Assert.Equal("第一条", entry.Body);
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM external_change_receipts "
            + "WHERE ChangeId = 'gateway-append-1'"));
    }

    [Fact]
    public async Task GatewayAppendFailure_RollsBackArchiveAndReceipt()
    {
        await RunProductionMigrationAsync();
        var archive = new ArchiveService(DbFactory);
        await archive.UpsertArchiveAsync(73, "原档案", 7.5, "原摘要");
        await using (var connection = await DbFactory.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO archive_entries(
                    EntryId, AnimeId, OccurredAt, EpisodeNumber, Body,
                    CreatedAt, UpdatedAt)
                VALUES(
                    'conflicting-entry', 73, '2026-01-01T00:00:00.0000000Z',
                    NULL, '已有内容', '2026-01-01T00:00:00.0000000Z',
                    '2026-01-01T00:00:00.0000000Z')
                """;
            await command.ExecuteNonQueryAsync();
        }

        var gateway = new PersonalAnimeDataGateway(
            DbFactory,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new NoOpNotificationService());
        var change = new PersonalAnimeChange(
            "conflicting-entry",
            PersonalAnimeChangeKind.AppendArchiveEntry,
            73,
            "新标题",
            "冲突回滚",
            Text: "不会提交");
        var result = await gateway.ApplyChangesAsync(
            new PersonalAnimeChangeSet("test-source", [change]));

        var restored = await archive.GetArchiveAsync(73);
        Assert.False(result.Results.Single().Applied);
        Assert.Equal("原档案", restored!.TitleSnapshot);
        Assert.Equal(7.5, restored.PersonalRating);
        Assert.Equal("原摘要", restored.SummaryNote);
        Assert.Equal(0, await CountAsync(
            "SELECT COUNT(*) FROM external_change_receipts "
            + "WHERE ChangeId = 'conflicting-entry'"));
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var connection = await DbFactory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = await DbFactory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task AssertTrackingEventAsync(
        string eventId,
        AnimeTrackingStatus previous,
        AnimeTrackingStatus next)
    {
        await using var connection = await DbFactory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PreviousStatus, NewStatus
            FROM tracking_events
            WHERE EventId = @eventId
            """;
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((int)previous, reader.GetInt32(0));
        Assert.Equal((int)next, reader.GetInt32(1));
    }

    private sealed class NoOpNotificationService : IAppNotificationService
    {
        public bool IsSupported => false;

        public bool NotificationsEnabled => false;

        public Task ScheduleAsync(
            AppNotificationRequest request,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CancelAsync(
            string group,
            string tag,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CancelGroupAsync(
            string group,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IDisposable RegisterActivationHandler(
            string category,
            Func<AppNotificationActivation, CancellationToken, Task> handler)
            => new Registration();

        public Task OpenNotificationSettingsAsync()
            => Task.CompletedTask;

        private sealed class Registration : IDisposable
        {
            public void Dispose() { }
        }
    }
}
