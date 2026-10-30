using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ArchiveQuerySchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();

    public enum QueryKind
    {
        Archives,
        Statistics,
        Review,
        Screenshots,
        Timeline,
        Detail,
    }

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(QueryKind.Archives)]
    [InlineData(QueryKind.Statistics)]
    [InlineData(QueryKind.Review)]
    [InlineData(QueryKind.Screenshots)]
    [InlineData(QueryKind.Timeline)]
    [InlineData(QueryKind.Detail)]
    [Trait("Category", "Mechanism")]
    public async Task PublicQueryReturnsWhileSqliteIsLockedAndFinishesWithoutTheCallingContext(QueryKind kind)
    {
        var queries = new ArchiveQueryService(new ArchiveService(_database.Factory));
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync<Task>(() => ReadAsync(queries, kind), context);
        try
        {
            var query = await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(query.IsCompleted);
            blocker.Dispose();
            await query.WaitAsync(Budget);
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            var query = await invocation.WaitAsync(Budget);
            await query.WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(QueryKind.Archives)]
    [InlineData(QueryKind.Statistics)]
    [InlineData(QueryKind.Review)]
    [InlineData(QueryKind.Screenshots)]
    [InlineData(QueryKind.Timeline)]
    [InlineData(QueryKind.Detail)]
    public async Task PublicQueryPreservesCancellation(QueryKind kind)
    {
        var queries = new ArchiveQueryService(new ArchiveService(_database.Factory));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAsync(queries, kind, cancellation.Token));
    }

    private static Task ReadAsync(ArchiveQueryService queries, QueryKind kind, CancellationToken ct = default)
        => kind switch
        {
            QueryKind.Archives => queries.LoadArchivesAsync(ct),
            QueryKind.Statistics => queries.LoadStatisticsAsync(ct),
            QueryKind.Review => queries.LoadReviewAsync(2026, ct),
            QueryKind.Screenshots => queries.LoadScreenshotsAsync(ct),
            QueryKind.Timeline => queries.LoadTimelineAsync(42, includePlayback: true, ct),
            QueryKind.Detail => queries.LoadDetailAsync(42, includePlayback: true, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
