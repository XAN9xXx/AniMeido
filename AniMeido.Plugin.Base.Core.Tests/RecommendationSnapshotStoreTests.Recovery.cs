using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed partial class RecommendationSnapshotStoreTests
{
    private static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedReadCannotDeleteTheReplacementSnapshot(bool identityOnly)
    {
        await _cache.SetCacheAsync(SnapshotKey, "{", TimeSpan.FromDays(90));
        var coldCache = new CacheService(_factory);
        using var store = new RecommendationSnapshotStore(coldCache);
        var replacement = Snapshot(2);
        var context = new PausedUiContext();
        using var blocker = await LockRecoveryDatabaseAsync();
        Task<object?>? reading = null;
        try
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                reading = ReadOldSnapshotAsync();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
            Assert.False(reading.IsCompleted);

            blocker.Dispose();
            await context.FirstPost.WaitAsync(RecoveryBudget);
            Assert.False(reading.IsCompleted);
            Assert.True(await store.TryWriteAsync(replacement, coldCache.CaptureGeneration(),
                store.Revision, CancellationToken.None).WaitAsync(RecoveryBudget));
            var replacementJson = await coldCache.GetCacheAllowExpiredAsync(SnapshotKey).WaitAsync(RecoveryBudget);
            Assert.NotNull(replacementJson);

            context.Resume();
            var oldResult = await reading.WaitAsync(RecoveryBudget);
            if (identityOnly)
                Assert.False(Assert.IsType<bool>(oldResult));
            else
                Assert.Null(oldResult);

            Assert.Equal(replacementJson,
                await coldCache.GetCacheAllowExpiredAsync(SnapshotKey).WaitAsync(RecoveryBudget));
            var restartedCache = new CacheService(_factory);
            Assert.Equal(replacementJson,
                await restartedCache.GetCacheAllowExpiredAsync(SnapshotKey).WaitAsync(RecoveryBudget));
            using var restarted = new RecommendationSnapshotStore(restartedCache);
            var restored = await restarted.ReadAsync(true, CancellationToken.None).WaitAsync(RecoveryBudget);
            Assert.NotNull(restored);
            Assert.Equal(replacement.GeneratedAt, restored.GeneratedAt);
            Assert.Equal(replacement.Items.Select(item => item.Anime.ID), restored.Items.Select(item => item.Anime.ID));
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            if (reading is not null)
            {
                await Task.WhenAny(reading).WaitAsync(RecoveryBudget);
                await ((Task)reading).ConfigureAwait(
                    ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        async Task<object?> ReadOldSnapshotAsync()
        {
            if (identityOnly)
                return await store.MatchesRoundAsync(replacement.GeneratedAt, CancellationToken.None);

            return await store.ReadAsync(true, CancellationToken.None);
        }
    }

    private async Task<SqliteConnection> LockRecoveryDatabaseAsync()
    {
        using (var pooled = _factory.CreateConnection())
            SqliteConnection.ClearPool(pooled);

        var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await blocker.OpenAsync();
            using var command = blocker.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = "PRAGMA journal_mode=DELETE";
            Assert.Equal("delete", await command.ExecuteScalarAsync());
            command.CommandText = "BEGIN EXCLUSIVE";
            await command.ExecuteNonQueryAsync();
            return blocker;
        }
        catch
        {
            blocker.Dispose();
            throw;
        }
    }

}
