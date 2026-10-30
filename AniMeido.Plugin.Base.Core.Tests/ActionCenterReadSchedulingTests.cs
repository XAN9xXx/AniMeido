using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ActionCenterReadSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly AnimePlan ExpectedPlan = new(
        42, "计划42", AnimePlanPriority.High, new DateOnly(2026, 11, 2), 7,
        At.AddDays(-2), At.AddDays(-1), At.AddHours(-1), At);
    private static readonly PlanReminder[] Reminders =
    [
        Absolute("old", 42, At.AddDays(-1).AddTicks(-1)),
        Absolute("lower-bound", 42, At.AddDays(-1)),
        Absolute("caught-up", 42, At.AddHours(-4)) with { CatchUpSentAt = At.AddHours(-2) },
        new("other-anime", 43, PlanReminderKind.RelativeToTargetDate, -2, new TimeOnly(20, 0),
            null, At.AddHours(-3), PlanReminderState.Pending, null, null),
        Absolute("handled", 42, At.AddHours(-2)) with
        {
            State = PlanReminderState.Handled,
            HandledAt = At.AddHours(-1),
        },
        Absolute("cancelled", 42, At.AddHours(-1)) with { State = PlanReminderState.Cancelled },
        new("upper-bound", 42, PlanReminderKind.RelativeToTargetDate, 0, new TimeOnly(12, 0),
            null, At, PlanReminderState.Pending, null, null),
        Absolute("future", 42, At.AddMinutes(1)),
    ];
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public enum ReadKind
    {
        Plan,
        Reminders,
        RecentUnprocessedReminders,
        Progress,
    }

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        var service = new ActionCenterService(Factory);
        await service.UpsertPlanAsync(42, "计划42", AnimePlanPriority.High, ExpectedPlan.TargetStartDate, 7);
        await service.UpsertPlanAsync(43, "计划43", AnimePlanPriority.Normal, null, 0);
        foreach (var reminder in Reminders)
            await service.AddReminderAsync(reminder);
        await service.RecordAsync(new AnimePlaybackProgress("progress42", 42, 4, 450, 900, false, At));
        await service.RecordAsync(new AnimePlaybackProgress("progress43", 43, 2, 300, 600, false, At.AddHours(-1)));

        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        // 固定夹具时间与归档字段，不复制查询或排序算法。
        command.CommandText = """
            UPDATE anime_plans SET CreatedAt=@created, UpdatedAt=@updated,
                StartedAt=@started, ArchivedAt=@archived WHERE AnimeId=42
            """;
        command.Parameters.AddWithValue("@created", ExpectedPlan.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@updated", ExpectedPlan.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("@started", ExpectedPlan.StartedAt!.Value.ToString("O"));
        command.Parameters.AddWithValue("@archived", ExpectedPlan.ArchivedAt!.Value.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(ReadKind.Plan)]
    [InlineData(ReadKind.Reminders)]
    [InlineData(ReadKind.RecentUnprocessedReminders)]
    [InlineData(ReadKind.Progress)]
    [Trait("Category", "Mechanism")]
    public async Task Read_ReturnsWhileSqliteIsLocked_AndPublishesCompleteResultsWithoutTheCallingContext(ReadKind kind)
    {
        var service = new ActionCenterService(Factory);
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync<Task>(() => RunAsync(service, kind), context);
        Task result;

        try
        {
            result = await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(result.IsCompleted);
            blocker.Dispose();
            await result.WaitAsync(CompletionBudget);
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            var pending = await invocation.WaitAsync(CompletionBudget);
            await pending.WaitAsync(CompletionBudget);
        }

        switch (kind)
        {
            case ReadKind.Plan:
                Assert.Equal(ExpectedPlan, await (Task<AnimePlan?>)result);
                Assert.Null(await service.GetPlanAsync(404));
                break;
            case ReadKind.Reminders:
                Assert.Equal(
                    new[] { Reminders[0], Reminders[1], Reminders[2], Reminders[6], Reminders[7] },
                    await (Task<IReadOnlyList<PlanReminder>>)result);
                Assert.Equal(Reminders, await service.GetRemindersAsync());
                break;
            case ReadKind.RecentUnprocessedReminders:
                Assert.Equal(new[] { Reminders[1], Reminders[3], Reminders[6] },
                    await (Task<IReadOnlyList<PlanReminder>>)result);
                Assert.Equal(new[] { Reminders[6] },
                    await service.GetRecentUnprocessedRemindersAsync(At, TimeSpan.Zero));
                break;
            case ReadKind.Progress:
                var snapshots = await (Task<IReadOnlyDictionary<int, AnimeProgressSnapshot>>)result;
                Assert.Equal(2, snapshots.Count);
                Assert.Equal(new AnimeProgressSnapshot(42, 4, 450, 900, At), snapshots[42]);
                Assert.Equal(new AnimeProgressSnapshot(43, 2, 300, 600, At.AddHours(-1)), snapshots[43]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Theory]
    [InlineData(ReadKind.Plan)]
    [InlineData(ReadKind.Reminders)]
    [InlineData(ReadKind.RecentUnprocessedReminders)]
    [InlineData(ReadKind.Progress)]
    public async Task Read_PreservesCancellationAndPropagatesRealSqliteFailures(ReadKind kind)
    {
        var service = new ActionCenterService(Factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(service, kind, cancellation.Token));

        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            var table = kind switch
            {
                ReadKind.Plan => "anime_plans",
                ReadKind.Progress => "anime_progress",
                _ => "plan_reminders",
            };
            command.CommandText = $"DROP TABLE {table}";
            await command.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<SqliteException>(() => RunAsync(service, kind));
        Assert.Equal(1, error.SqliteErrorCode);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    private static PlanReminder Absolute(string id, int animeId, DateTimeOffset scheduled)
        => new(id, animeId, PlanReminderKind.Absolute, null, null, scheduled, scheduled,
            PlanReminderState.Pending, null, null);

    private static Task RunAsync(ActionCenterService service, ReadKind kind, CancellationToken ct = default)
        => kind switch
        {
            ReadKind.Plan => service.GetPlanAsync(42, ct),
            ReadKind.Reminders => service.GetRemindersAsync(42, PlanReminderState.Pending, ct),
            ReadKind.RecentUnprocessedReminders => service.GetRecentUnprocessedRemindersAsync(At, TimeSpan.FromDays(1), ct),
            ReadKind.Progress => service.GetProgressAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
