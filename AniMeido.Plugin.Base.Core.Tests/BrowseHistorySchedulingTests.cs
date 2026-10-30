using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed partial class BrowseHistorySchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private readonly BrowseHistoryService _history;

    public BrowseHistorySchedulingTests()
    {
        _history = new BrowseHistoryService(_database.Factory);
    }

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(8)]
    [InlineData(200)]
    public async Task GetHistory_ReturnsWhileSqliteIsLocked_AndKeepsCompleteOrderedRowsForTodayAndTheme(int limit)
    {
        const int count = 205;
        await SeedHistoryAsync(count);
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync(() => _history.GetHistoryAsync(limit), context);
        Task<List<(int AnimeId, string? Title, DateTime LastViewed, int ViewCount)>>? query = null;
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

        var rows = await query!;
        Assert.Equal(Enumerable.Range(1, limit).Select(id => Expected(id, count)), rows);
        Assert.All(rows, row => Assert.Equal(DateTimeKind.Local, row.LastViewed.Kind));
        Assert.Contains(rows, row => row.Title is null);
    }

    [Fact]
    public async Task GetHistory_DefaultLimitStillReturnsTheLatestFiftyRows()
    {
        const int count = 51;
        await SeedHistoryAsync(count);

        var rows = await _history.GetHistoryAsync();

        Assert.Equal(Enumerable.Range(1, 50).Select(id => Expected(id, count)), rows);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 12)]
    public async Task GetHistory_KeepsExistingZeroAndNegativeSqliteLimitSemantics(int limit, int expectedCount)
    {
        const int count = 12;
        await SeedHistoryAsync(count);

        var rows = await _history.GetHistoryAsync(limit);

        Assert.Equal(Enumerable.Range(1, expectedCount).Select(id => Expected(id, count)), rows);
    }

    [Fact]
    public async Task GetHistory_PreCancelledRequestReturnsCancellationWithoutHoldingShutdownOpen()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _history.GetHistoryAsync(cancellationToken: cancellation.Token));
        await _database.Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task GetHistory_ExclusiveImportRejectsOutsideReadersButAllowsItsOwnQuery()
    {
        await SeedHistoryAsync(3);
        using var import = _database.Factory.Access.BeginExclusive();
        await import.WaitForDrainAsync(CancellationToken.None).WaitAsync(CompletionBudget);
        Task outside;
        using (ExecutionContext.SuppressFlow())
        {
            outside = Task.Run(() => _history.GetHistoryAsync());
        }

        await Assert.ThrowsAsync<PersonalDataBusyException>(() => outside.WaitAsync(CompletionBudget));
        var rows = await _history.GetHistoryAsync().WaitAsync(CompletionBudget);

        Assert.Equal(Enumerable.Range(1, 3).Select(id => Expected(id, 3)), rows);
    }

    [Fact]
    public async Task GetHistory_AfterDatabaseStopRejectsNewReaders()
    {
        await _database.Factory.StopAsync().WaitAsync(CompletionBudget);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => _history.GetHistoryAsync());
    }

    private async Task SeedHistoryAsync(int count)
    {
        for (var id = count; id > 0; id--)
        {
            await _history.RecordAsync(id, Title(id));
            for (var visit = 1; visit < Visits(id); visit++)
                await _history.RecordAsync(id, null);
        }

        await using var connection = await _database.Factory.OpenAsync();
        for (var id = 1; id <= count; id++)
        {
            // 只固定时钟元数据；标题保留和浏览次数由真实 RecordAsync 生成。
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE browse_history SET LastViewedAt=@viewed WHERE AnimeID=@id";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@viewed", ViewedAtUtc(id, count).ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static (int AnimeId, string? Title, DateTime LastViewed, int ViewCount) Expected(int id, int count)
        => (id, Title(id), ViewedAtUtc(id, count).ToLocalTime(), Visits(id));

    private static string? Title(int id) => id % 3 == 0 ? null : $"作品{id}";

    private static int Visits(int id) => id % 3 + 1;

    private static DateTime ViewedAtUtc(int id, int count)
        => new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(count - id);
}
