using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ActionCenterWriteSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlanReminder OriginalReminder = new(
        "existing-reminder", 42, PlanReminderKind.RelativeToTargetDate, -1, new TimeOnly(20, 0),
        null, At.AddDays(1), PlanReminderState.Pending, null, null);
    private static readonly PlanReminder AddedReminder = new(
        "new-reminder", 42, PlanReminderKind.Absolute, null, null, At.AddDays(2),
        At.AddDays(2), PlanReminderState.Pending, null, null);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public enum WriteKind
    {
        UpsertPlan,
        AddReminder,
        HandleReminder,
        MarkCatchUp,
        CancelReminder,
    }

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(WriteKind.UpsertPlan)]
    [InlineData(WriteKind.AddReminder)]
    [InlineData(WriteKind.HandleReminder)]
    [InlineData(WriteKind.MarkCatchUp)]
    [InlineData(WriteKind.CancelReminder)]
    [Trait("Category", "Mechanism")]
    public async Task Write_ReturnsWhileSqliteIsLocked_AndKeepsAdmissionUntilTheRealOperationEnds(WriteKind kind)
    {
        var (service, originalPlan) = await SeedAsync();
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        // 时间戳可以在等待 SQLite 锁前生成，下界覆盖整个业务调用。
        var notBefore = DateTimeOffset.UtcNow;
        var invocation = SchedulingTestThread.InvokeAsync<Task>(() => RunAsync(service, kind), context);
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
            if (kind == WriteKind.HandleReminder)
                Assert.True(await (Task<bool>)pending);
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

        await AssertSavedAsync(service, kind, originalPlan, notBefore);
    }

    [Theory]
    [InlineData(WriteKind.UpsertPlan)]
    [InlineData(WriteKind.AddReminder)]
    [InlineData(WriteKind.HandleReminder)]
    [InlineData(WriteKind.MarkCatchUp)]
    [InlineData(WriteKind.CancelReminder)]
    public async Task Write_PreCancelledRequestPreservesRecordsAndReleasesAdmission(WriteKind kind)
    {
        var (service, originalPlan) = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(service, kind, cancellation.Token));

        Assert.Equal(originalPlan, await service.GetPlanAsync(42));
        Assert.Equal(OriginalReminder, Assert.Single(await service.GetRemindersAsync()));
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Theory]
    [InlineData(WriteKind.UpsertPlan)]
    [InlineData(WriteKind.AddReminder)]
    [InlineData(WriteKind.HandleReminder)]
    [InlineData(WriteKind.MarkCatchUp)]
    [InlineData(WriteKind.CancelReminder)]
    public async Task Write_SqlFailurePreservesRecordsAndTheNextRealWriteCanFinish(WriteKind kind)
    {
        var (service, originalPlan) = await SeedAsync();
        var (table, action) = kind switch
        {
            WriteKind.UpsertPlan => ("anime_plans", "UPDATE"),
            WriteKind.AddReminder => ("plan_reminders", "INSERT"),
            _ => ("plan_reminders", "UPDATE"),
        };
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TRIGGER fail_action_write BEFORE {action} ON {table}
                BEGIN SELECT RAISE(ABORT, 'synthetic action write failure'); END
                """;
            await command.ExecuteNonQueryAsync();
        }

        var error = await Record.ExceptionAsync(() => RunAsync(service, kind));
        Assert.IsType<SqliteException>(error);
        Assert.Equal(originalPlan, await service.GetPlanAsync(42));
        Assert.Equal(OriginalReminder, Assert.Single(await service.GetRemindersAsync()));
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER fail_action_write";
            await command.ExecuteNonQueryAsync();
        }

        await RunAsync(service, kind).WaitAsync(CompletionBudget);
        await AssertSavedAsync(service, kind, originalPlan, DateTimeOffset.MinValue);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task AddReminder_ExistingIdentityUpdatesAllFields_WhileMissingUpdatesRemainNoOps()
    {
        var (service, originalPlan) = await SeedAsync();
        var replacement = new PlanReminder(
            OriginalReminder.ReminderId, 43, PlanReminderKind.Absolute, null, null,
            At.AddDays(3), At.AddDays(3), PlanReminderState.Handled, At.AddHours(1), At.AddHours(2));

        await service.AddReminderAsync(replacement);
        await service.MarkReminderCatchUpSentAsync("missing");
        await service.CancelReminderAsync("missing");

        Assert.False(await service.TryMarkReminderHandledAsync("missing"));
        Assert.Equal(replacement, Assert.Single(await service.GetRemindersAsync()));
        Assert.Empty(await service.GetRemindersAsync(42));
        Assert.Equal(replacement, Assert.Single(await service.GetRemindersAsync(43)));
        Assert.Equal(originalPlan, await service.GetPlanAsync(42));
    }

    private async Task<(ActionCenterService Service, AnimePlan OriginalPlan)> SeedAsync()
    {
        var service = new ActionCenterService(Factory);
        await service.UpsertPlanAsync(42, "原计划", AnimePlanPriority.High, new DateOnly(2026, 11, 2), 7);
        await service.UpsertPlanAsync(43, "另一计划", AnimePlanPriority.Normal, null, 0);
        await service.AddReminderAsync(OriginalReminder);
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            // 固定既有时间与归档字段，验证正常保存保留创建/开始时间并恢复有效计划。
            command.CommandText = """
                UPDATE anime_plans SET CreatedAt=@created, UpdatedAt=@updated,
                    StartedAt=@started, ArchivedAt=@archived WHERE AnimeId=42
                """;
            command.Parameters.AddWithValue("@created", At.AddDays(-2).ToString("O"));
            command.Parameters.AddWithValue("@updated", At.AddDays(-1).ToString("O"));
            command.Parameters.AddWithValue("@started", At.AddHours(-1).ToString("O"));
            command.Parameters.AddWithValue("@archived", At.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        return (service, Assert.IsType<AnimePlan>(await service.GetPlanAsync(42)));
    }

    private static Task RunAsync(ActionCenterService service, WriteKind kind, CancellationToken ct = default)
        => kind switch
        {
            WriteKind.UpsertPlan => service.UpsertPlanAsync(42, " 修改计划 ", AnimePlanPriority.Critical, null, 3, ct),
            WriteKind.AddReminder => service.AddReminderAsync(AddedReminder, ct),
            WriteKind.HandleReminder => service.TryMarkReminderHandledAsync(OriginalReminder.ReminderId, ct),
            WriteKind.MarkCatchUp => service.MarkReminderCatchUpSentAsync(OriginalReminder.ReminderId, ct),
            WriteKind.CancelReminder => service.CancelReminderAsync(OriginalReminder.ReminderId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static async Task AssertSavedAsync(
        ActionCenterService service, WriteKind kind, AnimePlan originalPlan, DateTimeOffset notBefore)
    {
        var plan = Assert.IsType<AnimePlan>(await service.GetPlanAsync(42));
        var reminders = await service.GetRemindersAsync();
        if (kind == WriteKind.UpsertPlan)
        {
            Assert.Equal(originalPlan with
            {
                TitleSnapshot = "修改计划",
                Priority = AnimePlanPriority.Critical,
                TargetStartDate = null,
                SortOrder = 3,
                UpdatedAt = plan.UpdatedAt,
                ArchivedAt = null,
            }, plan);
            Assert.True(plan.UpdatedAt >= notBefore);
            Assert.Equal(OriginalReminder, Assert.Single(reminders));
            return;
        }

        Assert.Equal(originalPlan, plan);
        if (kind == WriteKind.AddReminder)
        {
            Assert.Equal(2, reminders.Count);
            Assert.Equal(OriginalReminder, reminders.Single(row => row.ReminderId == OriginalReminder.ReminderId));
            Assert.Equal(AddedReminder, reminders.Single(row => row.ReminderId == AddedReminder.ReminderId));
            return;
        }

        var saved = Assert.Single(reminders);
        switch (kind)
        {
            case WriteKind.HandleReminder:
                Assert.Equal(OriginalReminder with { State = PlanReminderState.Handled, HandledAt = saved.HandledAt }, saved);
                Assert.True(saved.HandledAt >= notBefore);
                Assert.False(await service.TryMarkReminderHandledAsync(saved.ReminderId));
                Assert.Equal(saved, Assert.Single(await service.GetRemindersAsync()));
                break;
            case WriteKind.MarkCatchUp:
                Assert.Equal(OriginalReminder with { CatchUpSentAt = saved.CatchUpSentAt }, saved);
                Assert.True(saved.CatchUpSentAt >= notBefore);
                break;
            case WriteKind.CancelReminder:
                Assert.Equal(OriginalReminder with { State = PlanReminderState.Cancelled }, saved);
                await service.CancelReminderAsync(saved.ReminderId);
                Assert.Equal(saved, Assert.Single(await service.GetRemindersAsync()));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
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
