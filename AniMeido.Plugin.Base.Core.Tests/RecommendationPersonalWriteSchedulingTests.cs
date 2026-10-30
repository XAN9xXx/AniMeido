using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class RecommendationPersonalWriteSchedulingTests : IAsyncLifetime
{
    private const string SnapshotKey = "recommendations:metadata:snapshot:v1";
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private static readonly RecommendationFeature Feature = new(
        RecommendationFeatureKind.Tag, " tag:old ", " changed tag ");
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    public enum PersonalWriteKind
    {
        SetPreference,
        ClearPreferences,
        HideAnime,
        RestoreAnime,
        ClearHiddenAnime,
    }

    [Theory]
    [InlineData(PersonalWriteKind.SetPreference)]
    [InlineData(PersonalWriteKind.ClearPreferences)]
    [InlineData(PersonalWriteKind.HideAnime)]
    [InlineData(PersonalWriteKind.RestoreAnime)]
    [InlineData(PersonalWriteKind.ClearHiddenAnime)]
    public async Task PersonalWrite_ReturnsWhileSqliteIsLocked_AndOwnsTheCompleteWriteAndInvalidation(
        PersonalWriteKind kind)
    {
        await SeedPersonalDataAsync();
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync(SnapshotKey, "old snapshot", TimeSpan.FromDays(90));
        await using var candidates = RecommendationTestServices.CreateCandidates();
        await using var recommendations = RecommendationTestServices.CreateService(Factory, candidates, cache);
        using var blocker = await _database.LockExclusiveAsync();
        Func<Task> call = kind switch
        {
            PersonalWriteKind.SetPreference => () => recommendations.SetFeaturePreferenceAsync(
                Feature, RecommendationAdjustment.Reduce),
            PersonalWriteKind.ClearPreferences => () => recommendations.ClearFeaturePreferencesAsync(),
            PersonalWriteKind.HideAnime => () => recommendations.HideAnimeAsync(42, " changed's title "),
            PersonalWriteKind.RestoreAnime => () => recommendations.RestoreAnimeAsync(42),
            PersonalWriteKind.ClearHiddenAnime => () => recommendations.ClearHiddenAnimeAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var context = new PausedUiContext();
        var returned = SchedulingTestThread.InvokeAsync<Task>(call, context);
        IDisposable? import = null;
        try
        {
            var operation = await returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);
            var exclusive = Factory.Access.BeginExclusive();
            import = exclusive;
            var drained = exclusive.WaitForDrainAsync(CancellationToken.None);
            Assert.False(drained.IsCompleted);

            blocker.Dispose();
            await operation.WaitAsync(CompletionBudget);
            await drained.WaitAsync(CompletionBudget);
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            try
            {
                var pending = await returned.WaitAsync(CompletionBudget);
                await Task.WhenAny(pending).WaitAsync(CompletionBudget);
                await pending.ConfigureAwait(
                    ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
            finally
            {
                import?.Dispose();
            }
        }

        Assert.Null(await cache.GetCacheAllowExpiredAsync(SnapshotKey));
        var preferences = await recommendations.GetFeaturePreferencesAsync();
        var hidden = await recommendations.GetHiddenAnimeAsync();
        if (kind is PersonalWriteKind.SetPreference)
        {
            var changed = Assert.Single(preferences, preference => preference.Key == "tag:old");
            Assert.Equal("changed tag", changed.DisplayName);
            Assert.Equal(RecommendationAdjustment.Reduce, changed.Adjustment);
            Assert.True(changed.Invalid);
            Assert.Equal(2, preferences.Count);
        }
        else if (kind is PersonalWriteKind.ClearPreferences)
        {
            Assert.Empty(preferences);
        }
        else
        {
            Assert.Equal(new[] { "tag:old", "studio:1" }, preferences.Select(item => item.Key));
        }

        switch (kind)
        {
            case PersonalWriteKind.HideAnime:
                Assert.Equal("changed's title", Assert.Single(hidden, item => item.AnimeId == 42).Title);
                Assert.Equal(2, hidden.Count);
                break;
            case PersonalWriteKind.RestoreAnime:
                Assert.Equal(7, Assert.Single(hidden).AnimeId);
                break;
            case PersonalWriteKind.ClearHiddenAnime:
                Assert.Empty(hidden);
                break;
            default:
                Assert.Equal(new[] { 42, 7 }, hidden.Select(item => item.AnimeId));
                break;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedPersonalWrites_KeepSameInstanceInvocationOrder(bool hiddenAnime)
    {
        await SeedPersonalDataAsync();
        await using var candidates = RecommendationTestServices.CreateCandidates();
        await using var recommendations = RecommendationTestServices.CreateService(Factory, candidates);
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        Task[] writes = [];
        var returned = SchedulingTestThread.InvokeAsync<Task>(() =>
        {
            writes = hiddenAnime
                ? [
                    recommendations.HideAnimeAsync(42, "first title"),
                    recommendations.RestoreAnimeAsync(42),
                    recommendations.HideAnimeAsync(42, " last title "),
                ]
                : [
                    recommendations.SetFeaturePreferenceAsync(Feature, RecommendationAdjustment.Like),
                    recommendations.ClearFeaturePreferencesAsync(),
                    recommendations.SetFeaturePreferenceAsync(Feature, RecommendationAdjustment.Reduce),
                ];
            return Task.WhenAll(writes);
        }, context);
        try
        {
            var batch = await returned.WaitAsync(ReturnBudget);
            Assert.Equal(3, writes.Length);
            Assert.All(writes, write => Assert.False(write.IsCompleted));
            blocker.Dispose();
            await batch.WaitAsync(CompletionBudget);
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            var pending = await returned.WaitAsync(CompletionBudget);
            await Task.WhenAny(pending).WaitAsync(CompletionBudget);
            await pending.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        if (hiddenAnime)
        {
            var hidden = await recommendations.GetHiddenAnimeAsync();
            Assert.Equal("last title", Assert.Single(hidden, item => item.AnimeId == 42).Title);
            Assert.Equal(2, hidden.Count);
        }
        else
        {
            var preference = Assert.Single(await recommendations.GetFeaturePreferencesAsync());
            Assert.Equal("tag:old", preference.Key);
            Assert.Equal("changed tag", preference.DisplayName);
            Assert.Equal(RecommendationAdjustment.Reduce, preference.Adjustment);
            Assert.False(preference.Invalid);
            await recommendations.SetFeaturePreferenceAsync(Feature, null);
            Assert.Empty(await recommendations.GetFeaturePreferencesAsync());
        }
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task CommittedPersonalWrite_StillOwnsInvalidationWhenCancelledAndStopped()
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync(SnapshotKey, "old snapshot", TimeSpan.FromDays(90));
        await using var candidates = RecommendationTestServices.CreateCandidates();
        await using var recommendations = RecommendationTestServices.CreateService(Factory, candidates, cache);
        var snapshots = (RecommendationSnapshotStore)typeof(RecommendationService)
            .GetField("_snapshots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recommendations)!;
        var snapshotGate = (SemaphoreSlim)typeof(RecommendationSnapshotStore)
            .GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(snapshots)!;
        await snapshotGate.WaitAsync();
        var gateHeld = true;
        using var cancellation = new CancellationTokenSource();
        var saving = recommendations.SetFeaturePreferenceAsync(Feature,
            RecommendationAdjustment.Like, cancellation.Token);
        IDisposable? import = null;
        Task? stopping = null;
        try
        {
            // SQL 提交后才增加失效版本；此时失效操作正在等待快照锁。
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => snapshots.Revision == 1,
                CompletionBudget)));
            using (var connection = await Factory.OpenAsync())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT DisplayName FROM recommendation_feature_preferences WHERE FeatureKey='tag:old'";
                Assert.Equal("changed tag", await command.ExecuteScalarAsync());
            }

            var exclusive = Factory.Access.BeginExclusive();
            import = exclusive;
            var drained = exclusive.WaitForDrainAsync(CancellationToken.None);
            Assert.False(drained.IsCompleted);
            cancellation.Cancel();
            stopping = recommendations.DisposeAsync().AsTask();
            Assert.False(saving.IsCompleted);
            Assert.False(stopping.IsCompleted);

            gateHeld = false;
            snapshotGate.Release();
            await saving.WaitAsync(CompletionBudget);
            await drained.WaitAsync(CompletionBudget);
            await stopping.WaitAsync(CompletionBudget);
        }
        finally
        {
            if (gateHeld) snapshotGate.Release();
            try
            {
                await Task.WhenAny(saving).WaitAsync(CompletionBudget);
                await saving.ConfigureAwait(
                    ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                if (stopping is not null)
                {
                    await Task.WhenAny(stopping).WaitAsync(CompletionBudget);
                    await stopping.ConfigureAwait(
                        ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                }
            }
            finally
            {
                import?.Dispose();
            }
        }

        Assert.Null(await cache.GetCacheAllowExpiredAsync(SnapshotKey));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task FailedPersonalSql_ReleasesTheWriteGateWithoutInvalidating_AndAllowsTheNextWrite()
    {
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_fixture_preference
                BEFORE INSERT ON recommendation_feature_preferences
                WHEN NEW.FeatureKey='tag:reject'
                BEGIN SELECT RAISE(ABORT, 'fixture preference rejected'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var cache = new CacheService(Factory);
        await cache.SetCacheAsync(SnapshotKey, "old snapshot", TimeSpan.FromDays(90));
        await using var candidates = RecommendationTestServices.CreateCandidates();
        await using var recommendations = RecommendationTestServices.CreateService(Factory, candidates, cache);
        var snapshots = (RecommendationSnapshotStore)typeof(RecommendationService)
            .GetField("_snapshots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recommendations)!;
        var rejected = new RecommendationFeature(RecommendationFeatureKind.Tag, "tag:reject", "rejected tag");

        var failure = await Assert.ThrowsAsync<SqliteException>(() => recommendations
            .SetFeaturePreferenceAsync(rejected, RecommendationAdjustment.Like).WaitAsync(CompletionBudget));

        Assert.Contains("fixture preference rejected", failure.Message);
        Assert.Equal(0, snapshots.Revision);
        Assert.Equal("old snapshot", await cache.GetCacheAllowExpiredAsync(SnapshotKey));
        Assert.Empty(await recommendations.GetFeaturePreferencesAsync());

        await recommendations.SetFeaturePreferenceAsync(Feature, RecommendationAdjustment.Reduce)
            .WaitAsync(CompletionBudget);

        Assert.Equal(1, snapshots.Revision);
        Assert.Null(await cache.GetCacheAllowExpiredAsync(SnapshotKey));
        var saved = Assert.Single(await recommendations.GetFeaturePreferencesAsync());
        Assert.Equal("tag:old", saved.Key);
        Assert.Equal("changed tag", saved.DisplayName);
        Assert.Equal(RecommendationAdjustment.Reduce, saved.Adjustment);
        using var import = Factory.Access.BeginExclusive();
        await import.WaitForDrainAsync(CancellationToken.None).WaitAsync(CompletionBudget);
    }

    private async Task SeedPersonalDataAsync()
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recommendation_feature_preferences(
                FeatureKind, FeatureKey, DisplayName, Adjustment, UpdatedAt, Invalid)
            VALUES
                (0,'tag:old','old tag',1,'2026-10-20T11:00:00+00:00',1),
                (1,'studio:1','studio',-1,'2026-10-21T12:00:00+00:00',0);
            INSERT INTO recommendation_hidden_anime(AnimeId, TitleSnapshot, HiddenAt)
            VALUES
                (7,'older title','2026-10-20T11:00:00+00:00'),
                (42,'newer title','2026-10-21T12:00:00+00:00');
            """;
        await command.ExecuteNonQueryAsync();
    }

}
