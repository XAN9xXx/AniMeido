using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

public sealed partial class PersonalAnimeWriteTransactionTests
{
    [Fact]
    public async Task GatewaySqlFailure_ReportsTheOriginalFailureOnceAndLeavesTheArchiveUnchanged()
    {
        await RunProductionMigrationAsync();
        var archive = new ArchiveService(DbFactory);
        await archive.UpsertArchiveAsync(81, "原档案", 8.5, "原摘要");
        var original = await archive.GetArchiveAsync(81);
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_gateway_entry BEFORE INSERT ON archive_entries
                BEGIN SELECT RAISE(ABORT, 'synthetic gateway entry failure'); END
                """;
            await command.ExecuteNonQueryAsync();
        }
        var logger = new GatewayLogger();
        var gateway = GatewayWithLogger(logger);
        var change = AppendChange("failed-entry");

        var result = Assert.Single((await gateway.ApplyChangesAsync(new("test-source", [change]))).Results);

        Assert.False(result.Applied);
        Assert.False(result.WasAlreadyApplied);
        Assert.Equal(original, await archive.GetArchiveAsync(81));
        Assert.Empty(await archive.GetEntriesAsync(81));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM external_change_receipts"));
        var logged = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, logged.Level);
        var error = Assert.IsType<SqliteException>(logged.Exception);
        Assert.Contains("synthetic gateway entry failure", error.Message);
        Assert.Contains(change.ChangeId, logged.Message);
    }

    [Fact]
    public async Task GatewayInputRejection_DoesNotLogAnExpectedValidationError()
    {
        await RunProductionMigrationAsync();
        var logger = new GatewayLogger();
        var gateway = GatewayWithLogger(logger);
        var change = AppendChange("invalid-entry") with { EpisodeNumber = 0 };

        var result = Assert.Single((await gateway.ApplyChangesAsync(new("test-source", [change]))).Results);

        Assert.False(result.Applied);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM anime_archives"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM external_change_receipts"));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task GatewayPayloadConflict_DoesNotLogAnExpectedDuplicateRejection()
    {
        await RunProductionMigrationAsync();
        var logger = new GatewayLogger();
        var gateway = GatewayWithLogger(logger);
        var change = AppendChange("duplicate-entry");
        Assert.True(Assert.Single((await gateway.ApplyChangesAsync(new("test-source", [change]))).Results).Applied);

        var result = Assert.Single((await gateway.ApplyChangesAsync(new("test-source",
            [change with { Text = "冲突正文" }]))).Results);

        Assert.False(result.Applied);
        Assert.Equal("原感想", Assert.Single(await new ArchiveService(DbFactory).GetEntriesAsync(81)).Body);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM external_change_receipts"));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task GatewayCommittedNotificationFailure_StaysAppliedAndLogsOnlyOneWarning()
    {
        await RunProductionMigrationAsync();
        var failure = new InvalidOperationException("synthetic notification failure");
        var notifications = new NoOpNotificationService(() => throw failure);
        var logger = new GatewayLogger();
        var gateway = GatewayWithLogger(logger, notifications);
        var change = new PersonalAnimeChange("committed-status", PersonalAnimeChangeKind.SetTrackingStatus,
            81, "作品", "确认状态", TrackingStatus: AnimeTrackingStatus.Watching);

        var result = Assert.Single((await gateway.ApplyChangesAsync(new("test-source", [change]))).Results);

        Assert.True(result.Applied);
        Assert.False(result.WasAlreadyApplied);
        Assert.Equal(AnimeTrackingStatus.Watching,
            Assert.Single(await new TrackingService(DbFactory).GetAllTrackingAsync()).Status);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM external_change_receipts"));
        var logged = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Same(failure, logged.Exception);
        Assert.Contains(change.ChangeId, logged.Message);
    }

    private PersonalAnimeDataGateway GatewayWithLogger(
        GatewayLogger logger, IAppNotificationService? notifications = null)
        => new(DbFactory, dataSource: null!, tracking: null!, actionCenter: null!, archive: null!,
            savedTags: null!, browseHistory: null!, recommendations: null!, reminders: null!,
            notifications: notifications ?? new NoOpNotificationService(), logger: logger);

    private static PersonalAnimeChange AppendChange(string changeId)
        => new(changeId, PersonalAnimeChangeKind.AppendArchiveEntry, 81, "写入标题", "确认追加",
            Text: "原感想", EpisodeNumber: 3, OccurredAt: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class GatewayLogger : ILogger<PersonalAnimeDataGateway>
    {
        internal List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
