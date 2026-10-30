using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class RecommendationPersonalReadSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    public enum PersonalReadKind
    {
        Preferences,
        HiddenAnime,
    }

    [Theory]
    [InlineData(PersonalReadKind.Preferences)]
    [InlineData(PersonalReadKind.HiddenAnime)]
    public async Task PersonalRead_ReturnsWhileSqliteIsLocked_AndFinishesWithoutTheCallingContext(
        PersonalReadKind kind)
    {
        await SeedPersonalDataAsync();
        await using var candidates = RecommendationTestServices.CreateCandidates();
        await using var recommendations = RecommendationTestServices.CreateService(Factory, candidates);
        using var blocker = await _database.LockExclusiveAsync();
        Func<Task> call = kind switch
        {
            PersonalReadKind.Preferences => () => recommendations.GetFeaturePreferencesAsync(),
            PersonalReadKind.HiddenAnime => () => recommendations.GetHiddenAnimeAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var context = new PausedUiContext();
        var returned = SchedulingTestThread.InvokeAsync<Task>(call, context);
        Task operation;
        try
        {
            operation = await returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);

            blocker.Dispose();
            await operation.WaitAsync(CompletionBudget);
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

        if (kind == PersonalReadKind.Preferences)
        {
            Assert.Equal(new[]
            {
                new RecommendationFeaturePreference(RecommendationFeatureKind.Tag,
                    "tag:alpha", "Alpha", RecommendationAdjustment.Like,
                    new DateTimeOffset(2026, 10, 20, 11, 0, 0, TimeSpan.Zero)),
                new RecommendationFeaturePreference(RecommendationFeatureKind.Tag,
                    "tag:beta", "beta", RecommendationAdjustment.Reduce,
                    new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero), Invalid: true),
                new RecommendationFeaturePreference(RecommendationFeatureKind.Studio,
                    "studio:1", "A studio", RecommendationAdjustment.Reduce,
                    new DateTimeOffset(2026, 10, 22, 13, 0, 0, TimeSpan.Zero)),
                new RecommendationFeaturePreference(RecommendationFeatureKind.VoiceActor,
                    "voice:2", "An actor", RecommendationAdjustment.Like,
                    new DateTimeOffset(2026, 10, 23, 14, 0, 0, TimeSpan.Zero)),
            }, await (Task<IReadOnlyList<RecommendationFeaturePreference>>)operation);
        }
        else
        {
            Assert.Equal(new[]
            {
                new RecommendationHiddenAnime(42, "newest's title",
                    new DateTimeOffset(2026, 10, 23, 14, 0, 0, TimeSpan.Zero)),
                new RecommendationHiddenAnime(99, "middle title",
                    new DateTimeOffset(2026, 10, 22, 13, 0, 0, TimeSpan.Zero)),
                new RecommendationHiddenAnime(7, "oldest title",
                    new DateTimeOffset(2026, 10, 20, 11, 0, 0, TimeSpan.Zero)),
            }, await (Task<IReadOnlyList<RecommendationHiddenAnime>>)operation);
        }
    }

    [Fact]
    public async Task PersonalReads_RejectAnAlreadyCancelledRequestWithoutWaitingForSqlite()
    {
        await using var candidates = RecommendationTestServices.CreateCandidates();
        await using var recommendations = RecommendationTestServices.CreateService(Factory, candidates);
        using var blocker = await _database.LockExclusiveAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recommendations.GetFeaturePreferencesAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recommendations.GetHiddenAnimeAsync(cancellation.Token));
    }

    private async Task SeedPersonalDataAsync()
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recommendation_feature_preferences(
                FeatureKind, FeatureKey, DisplayName, Adjustment, UpdatedAt, Invalid)
            VALUES
                (1,'studio:1','A studio',-1,'2026-10-22T13:00:00+00:00',0),
                (0,'tag:beta','beta',-1,'2026-10-21T12:00:00+00:00',1),
                (2,'voice:2','An actor',1,'2026-10-23T14:00:00+00:00',0),
                (0,'tag:alpha','Alpha',1,'2026-10-20T11:00:00+00:00',0);
            INSERT INTO recommendation_hidden_anime(AnimeId, TitleSnapshot, HiddenAt)
            VALUES
                (7,'oldest title','2026-10-20T11:00:00+00:00'),
                (42,'newest''s title','2026-10-23T14:00:00+00:00'),
                (99,'middle title','2026-10-22T13:00:00+00:00');
            """;
        await command.ExecuteNonQueryAsync();
    }

}
