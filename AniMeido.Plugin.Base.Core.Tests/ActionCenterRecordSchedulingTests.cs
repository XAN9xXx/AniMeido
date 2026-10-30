using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ActionCenterRecordSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private static readonly AnimePlaybackProgress Progress = new(
        "record-event", 42, 1, 540, 600, false,
        new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task Record_ReturnsWhileSqliteIsLocked_AndDrainsOnlyAfterTheWholeTransactionEnds()
    {
        var service = new ActionCenterService(Factory);
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync<Task>(() => service.RecordAsync(Progress), context);
        PersonalDataAccess.Operation? import = null;

        try
        {
            var pending = await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(pending.IsCompleted);
            import = Factory.Access.BeginExclusive();
            var drained = import.WaitForDrainAsync(CancellationToken.None);
            Assert.False(drained.IsCompleted);

            blocker.Dispose();
            await pending.WaitAsync(CompletionBudget);
            await drained.WaitAsync(CompletionBudget);
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            try
            {
                await FinishInvocationAsync(invocation);
            }
            finally
            {
                import?.Dispose();
            }
        }

        await AssertRowCountsAsync(1);
        var saved = (await service.GetProgressAsync())[42];
        Assert.Equal(Progress.EpisodeNumber, saved.CurrentEpisode);
        Assert.Equal(Progress.PositionSeconds, saved.PositionSeconds);
        Assert.Equal(Progress.DurationSeconds, saved.DurationSeconds);
        Assert.Equal(Progress.ObservedAt, saved.LastWatchedAt);
    }

    [Theory]
    [InlineData(0, 1, 540, 600)]
    [InlineData(42, 0, 540, 600)]
    [InlineData(42, 1, 270, 299)]
    [InlineData(42, 1, -1, 600)]
    public async Task Record_InvalidProgressReturnsBeforeCancellationAndDoesNotWrite(
        int animeId, int episodeNumber, double position, double duration)
    {
        var service = new ActionCenterService(Factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await service.RecordAsync(Progress with
        {
            AnimeId = animeId,
            EpisodeNumber = episodeNumber,
            PositionSeconds = position,
            DurationSeconds = duration,
        }, cancellation.Token);

        await AssertRowCountsAsync(0);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task Record_NullProgressPreservesValidationAndReleasesAdmission()
    {
        var service = new ActionCenterService(Factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.RecordAsync(null!, cancellation.Token));

        Assert.Equal("progress", error.ParamName);
        await AssertRowCountsAsync(0);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task Record_PreCancelledValidProgressDoesNotWrite_AndTheNextRecordCanFinish()
    {
        var service = new ActionCenterService(Factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RecordAsync(Progress, cancellation.Token));

        await AssertRowCountsAsync(0);
        await service.RecordAsync(Progress).WaitAsync(CompletionBudget);
        await AssertRowCountsAsync(1);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task Record_LastWriteFailureRollsBackAllTables_AndTheSameEventCanBeRetried()
    {
        var service = new ActionCenterService(Factory);
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_record_tracking BEFORE INSERT ON tracking
                BEGIN SELECT RAISE(ABORT, 'synthetic tracking write failure'); END
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => service.RecordAsync(Progress));

        await AssertRowCountsAsync(0);
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER fail_record_tracking";
            await command.ExecuteNonQueryAsync();
        }

        await service.RecordAsync(Progress).WaitAsync(CompletionBudget);
        await AssertRowCountsAsync(1);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task Record_CompletionThresholdRemainsExactlyNinetyPercent()
    {
        var service = new ActionCenterService(Factory);
        await service.RecordAsync(Progress with
        {
            EventId = "below-threshold",
            PositionSeconds = 539,
        });
        await service.RecordAsync(Progress with { EpisodeNumber = 2 });

        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EpisodeNumber, IsCompleted FROM episode_progress
            WHERE AnimeId = 42 ORDER BY EpisodeNumber
            """;
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.False(await reader.ReadAsync());
    }

    private async Task AssertRowCountsAsync(int expected)
    {
        using var connection = await Factory.OpenAsync();
        foreach (var table in new[] { "watch_sessions", "episode_progress", "anime_progress", "tracking" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            Assert.Equal(expected, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
    }

    private static async Task FinishInvocationAsync(Task<Task> invocation)
    {
        var pending = await invocation.WaitAsync(CompletionBudget);
        try
        {
            await pending.WaitAsync(CompletionBudget);
        }
        catch (SqliteException) when (pending.IsFaulted)
        {
            // 正文报告 SQL 失败；收尾仍等待实际任务，不吞等待超时。
        }
    }
}
