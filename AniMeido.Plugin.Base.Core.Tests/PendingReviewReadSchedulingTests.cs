using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed class PendingReviewReadSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_ReturnsWhileSqliteIsLocked_AndCompletesWithoutTheCallingContext(bool localTargets)
    {
        await SeedAsync();
        var service = CreateService();
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var returned = SchedulingTestThread.InvokeAsync<Task>(
            () => localTargets ? service.GetLocalTargetsAsync() : service.GetItemsAsync(), context);
        Task result;

        try
        {
            result = await returned.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(result.IsCompleted);
            blocker.Dispose();
            await result.WaitAsync(CompletionBudget);
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            var pending = await returned.WaitAsync(CompletionBudget);
            await pending.WaitAsync(CompletionBudget);
        }

        if (localTargets)
        {
            var targets = await (Task<IReadOnlyList<LocalAnimeTarget>>)result;
            Assert.Equal(new[] { -7, 10, 99 }, targets.Select(target => target.AnimeId).Order());
            Assert.Equal("档案标题", targets.Single(target => target.AnimeId == -7).Title);
            Assert.Equal("来源标题", targets.Single(target => target.AnimeId == 10).Title);
            Assert.Equal("未命名作品", targets.Single(target => target.AnimeId == 99).Title);
        }
        else
        {
            var item = Assert.Single(await (Task<IReadOnlyList<PendingReviewItem>>)result);
            Assert.Equal(10, item.AnimeId);
            Assert.Equal("来源标题", item.Title);
            Assert.Equal("merged_conflict", item.Reason);
            Assert.Equal(-7, item.MergedInto);
            Assert.Equal("档案标题", item.MergedIntoTitle);
            Assert.Equal("2026-10-26", item.KeptAt);
            Assert.Equal(1, item.RecordCounts["tracking"]);
            Assert.Equal(1, item.RecordCounts["anime_archives"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_PreservesCancellationAndRealSqliteFailures(bool localTargets)
    {
        await SeedAsync();
        var service = CreateService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> cancelled = () => localTargets
            ? service.GetLocalTargetsAsync(cancellation.Token)
            : service.GetItemsAsync(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(cancelled);

        using (var connection = await Factory.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = localTargets ? "DROP TABLE tracking" : "DROP TABLE public_id_issues";
            await command.ExecuteNonQueryAsync();
        }
        Func<Task> failing = () => localTargets ? service.GetLocalTargetsAsync() : service.GetItemsAsync();
        var error = await Assert.ThrowsAsync<SqliteException>(failing);
        Assert.Equal(1, error.SqliteErrorCode);
    }

    private PendingReviewService CreateService()
        => new(Factory, new PersonalIdRemapService(Factory, NullLogger<PersonalIdRemapService>.Instance));

    private async Task SeedAsync()
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tracking(AnimeId,Status,UpdatedAt) VALUES
                (-7,1,'2026-10-26'), (10,1,'2026-10-26'), (99,1,'2026-10-26');
            INSERT INTO anime_archives(AnimeId,TitleSnapshot,CreatedAt,UpdatedAt) VALUES
                (-7,'档案标题','2026-10-26','2026-10-26'),
                (10,'来源标题','2026-10-26','2026-10-26');
            INSERT INTO public_id_issues(AnimeId,Kind,MergedInto,TitleSnapshot,DetectedAt,KeptAt)
                VALUES(10,'merged_conflict',-7,NULL,'2026-10-26','2026-10-26');
            """;
        await command.ExecuteNonQueryAsync();
    }

}
