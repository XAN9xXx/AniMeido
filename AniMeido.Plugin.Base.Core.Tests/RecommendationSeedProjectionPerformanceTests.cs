using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class RecommendationSeedProjectionPerformanceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int SummaryLength = 8192;
    private const int WarmupRounds = 3;
    private const int MeasurementRounds = 5;
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [Trait("Category", "Performance")]
    public async Task Refresh_UsesTheLatestThirtyEqualWeightArchiveSeeds_AndReportsItsCost(int count)
    {
        await SeedArchivesAsync(count);
        var source = new SeedCaptureSource();
        await using var candidates = new RecommendationCandidateProvider(
            source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var recommendations = RecommendationTestServices.CreateService(_database.Factory, candidates);
        var expected = Enumerable.Range(count - 29, 30)
            .Select(id => new ExpectedSeed(id, ArchiveTitle(id), 3, 3,
                RecommendationEvidenceSource.PersonalRating))
            .ToArray();

        for (var round = 0; round < WarmupRounds; round++)
        {
            source.ResetReads();
            var generation = await recommendations.RefreshAsync(preferNewBatch: true);
            AssertSeeds(expected, source, generation);
            Assert.Empty(source.DetailReads);
        }

        var times = new double[MeasurementRounds];
        var allocations = new long[MeasurementRounds];
        for (var round = 0; round < MeasurementRounds; round++)
        {
            source.ResetReads();
            var beforeBytes = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var generation = await recommendations.RefreshAsync(preferNewBatch: true);
            times[round] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocations[round] = GC.GetTotalAllocatedBytes(precise: true) - beforeBytes;

            AssertSeeds(expected, source, generation);
            Assert.Empty(source.DetailReads);
        }

        output.WriteLine(
            "Archives={0}; SummaryChars={1}; RatedArchives={0}; ExpectedSeeds=30; Warmups={2}; Measurements={3}",
            count, SummaryLength, WarmupRounds, MeasurementRounds);
        output.WriteLine("RefreshSamplesMs={0}",
            string.Join(",", times.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))));
        output.WriteLine("ProcessAllocatedByteSamples={0}", string.Join(",", allocations));
        output.WriteLine("Refresh: MedianTotalMs={0:F3}; MedianProcessAllocatedBytes={1}",
            times.Order().ElementAt(MeasurementRounds / 2),
            allocations.Order().ElementAt(MeasurementRounds / 2));
        output.WriteLine(
            "Real public RefreshAsync(preferNewBatch: true), production schema, synthetic warm database, no network. "
            + "Each refresh checks all 30 unique-tag profile entries and seed reads. Setup and assertions excluded. "
            + "Run this class alone: allocation samples cover the whole process across async work and may include background noise. "
            + "This is service timing, not UI frame timing.");
    }

    [Fact]
    public async Task Refresh_PreservesRatingSignalsTitleFallbackAndSeedExclusions()
    {
        await SeedControlArchivesAsync();
        var source = new SeedCaptureSource();
        await using var candidates = new RecommendationCandidateProvider(
            source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var recommendations = RecommendationTestServices.CreateService(_database.Factory, candidates);

        var generation = await recommendations.RefreshAsync(preferNewBatch: true);

        AssertSeeds(
        [
            new ExpectedSeed(1, "档案标题1", 4, 2.4, RecommendationEvidenceSource.PersonalRating),
            new ExpectedSeed(2, "低分标题2", -3, -3, RecommendationEvidenceSource.PersonalRating),
            new ExpectedSeed(4, RemoteTitle(4), 1, 1, RecommendationEvidenceSource.Following),
            new ExpectedSeed(6, RemoteTitle(6), 3, 3, RecommendationEvidenceSource.PersonalRating),
        ], source, generation);
        Assert.Equal(new[] { 4, 6 }, source.DetailReads.Order());
    }

    private async Task SeedArchivesAsync(int count)
    {
        using var connection = await _database.Factory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO anime_archives(AnimeId, TitleSnapshot, PersonalRating, SummaryNote, CreatedAt, UpdatedAt)
            VALUES(@id, @title, 8.5, @summary, @at, @at);
            """;
        var id = command.Parameters.AddWithValue("@id", 0);
        var title = command.Parameters.AddWithValue("@title", string.Empty);
        var at = command.Parameters.AddWithValue("@at", string.Empty);
        command.Parameters.AddWithValue("@summary", new string('文', SummaryLength));
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 1; index <= count; index++)
        {
            id.Value = index;
            title.Value = ArchiveTitle(index);
            at.Value = first.AddMinutes(index).ToString("O", CultureInfo.InvariantCulture);
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    private async Task SeedControlArchivesAsync()
    {
        using var connection = await _database.Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO anime_archives(AnimeId, TitleSnapshot, PersonalRating, SummaryNote, CreatedAt, UpdatedAt)
            VALUES
                (1, '档案标题1', 8.5, '', @at, @at),
                (2, '低分标题2', 2.5, '', @at, @at),
                (3, '零贡献标题3', 5.5, '', @at, @at),
                (4, '', NULL, '', @at, @at),
                (5, '屏蔽标题5', 8.5, '', @at, @at),
                (6, '   ', 8.5, '', @at, @at),
                (7, '未评分标题7', NULL, '', @at, @at),
                (-1, '本地标题', 10.0, '', @at, @at);
            INSERT INTO tracking(AnimeId, Status, UpdatedAt)
            VALUES(1, @completed, @at), (4, @following, @at), (5, @blocked, @at);
            """;
        command.Parameters.AddWithValue("@at", "2026-10-29T12:00:00.0000000+00:00");
        command.Parameters.AddWithValue("@completed", (int)AnimeTrackingStatus.Completed);
        command.Parameters.AddWithValue("@following", (int)AnimeTrackingStatus.Following);
        command.Parameters.AddWithValue("@blocked", (int)AnimeTrackingStatus.Blocked);
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertSeeds(
        IReadOnlyList<ExpectedSeed> expected,
        SeedCaptureSource source,
        RecommendationGeneration? generation)
    {
        Assert.NotNull(generation);
        Assert.Empty(generation.Snapshot.Items);
        Assert.Equal(expected.Count, generation.Profile.Count);
        Assert.Equal(expected.Select(item => item.AnimeId).Order(), source.FeatureReads.Order());
        foreach (var item in expected)
        {
            var profile = Assert.Single(generation.Profile, profile =>
                profile.Feature.Key == TagName(item.AnimeId).ToUpperInvariant());
            Assert.Equal(RecommendationFeatureKind.Tag, profile.Feature.Kind);
            Assert.Equal(TagName(item.AnimeId), profile.Feature.DisplayName);
            Assert.Equal(item.Weight, profile.InferredScore, 12);
            Assert.Equal(item.Weight, profile.EffectiveScore, 12);
            Assert.Null(profile.Adjustment);
            Assert.False(profile.IsSavedTag);
            var evidence = Assert.Single(profile.Evidence);
            Assert.Equal(item.AnimeId, evidence.AnimeId);
            Assert.Equal(item.Title, evidence.Title);
            Assert.Equal(item.Contribution, evidence.Contribution, 12);
            Assert.Equal(item.Source, evidence.Source);
        }
    }

    private static string ArchiveTitle(int id) => FormattableString.Invariant($"档案作品{id:D4}");
    private static string RemoteTitle(int id) => FormattableString.Invariant($"远端标题{id:D4}");
    private static string TagName(int id) => FormattableString.Invariant($"seed-{id:D4}");

    private sealed record ExpectedSeed(
        int AnimeId,
        string Title,
        double Weight,
        double Contribution,
        RecommendationEvidenceSource Source);

    private sealed class SeedCaptureSource : IAnimeDataSource
    {
        internal ConcurrentQueue<int> FeatureReads { get; } = new();
        internal ConcurrentQueue<int> DetailReads { get; } = new();

        internal void ResetReads()
        {
            FeatureReads.Clear();
            DetailReads.Clear();
        }

        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
        {
            FeatureReads.Enqueue(animeID);
            return Task.FromResult(new List<Tag> { new(TagName(animeID)) });
        }

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
        {
            DetailReads.Enqueue(animeID);
            return Task.FromResult<Anime?>(new Anime(
                animeID, RemoteTitle(animeID), null, [], null, null, string.Empty, 2026, 1));
        }

        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
            => Task.FromResult(new List<Studio>());
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
            => Task.FromResult(new List<VoiceActor>());
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(
            string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null)
            => Task.FromResult<(List<Anime>, int)>(([], 0));
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => Task.FromResult(new List<Anime>());
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(
            string keyword, int offset, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
