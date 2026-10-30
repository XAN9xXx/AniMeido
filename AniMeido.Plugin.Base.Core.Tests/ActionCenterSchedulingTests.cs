using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ActionCenterSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private static readonly AnimePlan[] Plans =
    [
        Plan(1, AnimePlanPriority.Normal, new DateOnly(2026, 10, 1), 1, 8),
        Plan(2, AnimePlanPriority.Critical, null, 0, 7),
        Plan(3, AnimePlanPriority.High, new DateOnly(2026, 10, 3), 2, 9, Timestamp(10)),
        Plan(4, AnimePlanPriority.High, new DateOnly(2026, 10, 3), 1, 5),
        Plan(5, AnimePlanPriority.High, new DateOnly(2026, 10, 3), 1, 6),
        Plan(6, AnimePlanPriority.High, null, 0, 4),
        Plan(7, AnimePlanPriority.High, new DateOnly(2026, 10, 2), 0, 3, Timestamp(19), Timestamp(20)),
        Plan(8, AnimePlanPriority.Normal, null, 99, 2),
    ];

    private readonly PersonalDatabaseFixture _database = new();
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly ActionCenterService _service;

    public ActionCenterSchedulingTests()
    {
        _dbFactory = _database.Factory;
        _service = new ActionCenterService(_dbFactory);
    }

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();

        foreach (var plan in Plans)
        {
            await _service.UpsertPlanAsync(
                plan.AnimeId, plan.TitleSnapshot, plan.Priority, plan.TargetStartDate, plan.SortOrder);
        }
        await using var seedConnection = await _dbFactory.OpenAsync();
        foreach (var plan in Plans)
        {
            // 固定时间和归档字段仅准备读夹具，不复写产品的筛选与排序规则。
            await using var command = seedConnection.CreateCommand();
            command.CommandText = """
                UPDATE anime_plans SET CreatedAt=@created, UpdatedAt=@updated,
                    StartedAt=@started, ArchivedAt=@archived WHERE AnimeId=@id
                """;
            command.Parameters.AddWithValue("@id", plan.AnimeId);
            command.Parameters.AddWithValue("@created", plan.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("@updated", plan.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("@started", (object?)plan.StartedAt?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("@archived", (object?)plan.ArchivedAt?.ToString("O") ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPlans_ReturnsWhileSqliteIsLocked_AndPreservesEveryFieldAndOrdering(bool includeArchived)
    {
        using var blocker = await LockDatabaseAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync(() => _service.GetPlansAsync(includeArchived), context);
        Task<IReadOnlyList<AnimePlan>>? query = null;
        try
        {
            query = await invocation.WaitAsync(ReturnBudget);
            Assert.False(query.IsCompleted);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            var actualQuery = await invocation.WaitAsync(CompletionBudget);
            await actualQuery.WaitAsync(CompletionBudget);
        }

        var expectedIds = includeArchived
            ? new[] { 2, 7, 5, 4, 3, 6, 1, 8 }
            : new[] { 2, 5, 4, 3, 6, 1, 8 };
        Assert.Equal(expectedIds.Select(id => Plans.Single(plan => plan.AnimeId == id)), await query!);
    }

    [Fact]
    public async Task GetPlans_PreCancelledRequestReturnsCancellationWithoutHoldingShutdownOpen()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.GetPlansAsync(cancellationToken: cancellation.Token));
        await _dbFactory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task GetPlans_ExclusiveImportRejectsOutsideReadersButAllowsItsOwnQuery()
    {
        using var import = _dbFactory.Access.BeginExclusive();
        await import.WaitForDrainAsync(CancellationToken.None).WaitAsync(CompletionBudget);
        Task<IReadOnlyList<AnimePlan>> outside;
        using (ExecutionContext.SuppressFlow())
        {
            outside = Task.Run(() => _service.GetPlansAsync(includeArchived: true));
        }

        await Assert.ThrowsAsync<PersonalDataBusyException>(() => outside.WaitAsync(CompletionBudget));
        var inside = await _service.GetPlansAsync(includeArchived: true).WaitAsync(CompletionBudget);

        Assert.Equal(new[] { 2, 7, 5, 4, 3, 6, 1, 8 }, inside.Select(plan => plan.AnimeId));
    }

    [Fact]
    public async Task GetPlans_AfterDatabaseStopRejectsNewReaders()
    {
        await _dbFactory.StopAsync().WaitAsync(CompletionBudget);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            _service.GetPlansAsync(includeArchived: true));
    }

    private Task<SqliteConnection> LockDatabaseAsync() => _database.LockExclusiveAsync();

    private static DateTimeOffset Timestamp(int day)
        => new(2026, 10, day, 12, 34, 56, TimeSpan.Zero);

    private static AnimePlan Plan(
        int id,
        AnimePlanPriority priority,
        DateOnly? target,
        int sortOrder,
        int updatedDay,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? archivedAt = null)
        => new(id, $"计划{id}", priority, target, sortOrder,
            Timestamp(1), Timestamp(updatedDay), startedAt, archivedAt);
}
