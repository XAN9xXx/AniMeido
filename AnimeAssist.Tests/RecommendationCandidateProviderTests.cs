using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public class RecommendationCandidateProviderTests
{
    [Fact]
    public async Task GetFeaturesAsync_LimitsAllRemoteRequestsToFour()
    {
        var dataSource = new ConcurrentFeatureDataSource();
        using var provider = new RecommendationCandidateProvider(
            dataSource,
            NullLogger<RecommendationCandidateProvider>.Instance);
        var seeds = Enumerable.Range(1, 12)
            .Select(id => new RecommendationSeed(id, $"番剧 {id}", 1))
            .ToArray();

        var result = await provider.GetFeaturesAsync(
            seeds,
            CancellationToken.None);

        Assert.Equal(12, result.Count);
        Assert.InRange(dataSource.MaximumConcurrency, 2, 4);
        Assert.All(result.Values, features => Assert.Equal(3, features.Count));
    }

    [Theory]
    [InlineData(" 科 幻 ", "科 幻")]
    [InlineData("Sci-Fi", "SCI-FI")]
    public void NormalizeTag_IsStable(string input, string expected)
        => Assert.Equal(expected, RecommendationCandidateProvider.NormalizeTag(input));

    [Fact]
    public async Task TagCandidate_PreservesSourceFeatureForFinalScoring()
    {
        using var provider = new RecommendationCandidateProvider(
            new TagCandidateDataSource(),
            NullLogger<RecommendationCandidateProvider>.Instance);
        var profile = RecommendationScorer.BuildProfile(
            [],
            new Dictionary<int, IReadOnlyList<RecommendationFeature>>(),
            [],
            ["科幻"]);

        var candidates = await provider.GetCandidatesAsync(
            profile,
            new HashSet<int>(),
            CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Contains(
            candidate.Features,
            feature => feature.Kind == RecommendationFeatureKind.Tag
                && feature.Key == "科幻");
        var result = RecommendationScorer.Rank(
            profile,
            candidates,
            new DateOnly(2026, 8, 1));
        Assert.Contains("收藏了 Tag", Assert.Single(result).ReasonSummary);
    }

    [Fact]
    public async Task SavedTag_IsNotDisplacedByHigherInferredSourceTags()
    {
        var dataSource = new TagCandidateDataSource();
        using var provider = new RecommendationCandidateProvider(
            dataSource,
            NullLogger<RecommendationCandidateProvider>.Instance);
        var profile = Enumerable.Range(0, 7)
            .Select(index => new RecommendationFeatureProfile(
                new RecommendationFeature(
                    RecommendationFeatureKind.Tag,
                    $"INFERRED-{index}",
                    $"推断标签 {index}"),
                10 - index,
                null,
                []))
            .Append(new RecommendationFeatureProfile(
                new RecommendationFeature(
                    RecommendationFeatureKind.Tag,
                    "百合",
                    "百合"),
                2.5,
                null,
                [],
                IsSavedTag: true))
            .ToArray();

        await provider.GetCandidatesAsync(
            profile,
            new HashSet<int>(),
            CancellationToken.None);

        Assert.Contains("百合", dataSource.SearchedTags);
    }

    [Fact]
    public async Task SavedTag_ReservesCandidatesForFeatureEnrichment()
    {
        using var provider = new RecommendationCandidateProvider(
            new DistinctTagCandidateDataSource(),
            NullLogger<RecommendationCandidateProvider>.Instance);
        var profile = Enumerable.Range(0, 7)
            .Select(index => new RecommendationFeatureProfile(
                new RecommendationFeature(
                    RecommendationFeatureKind.Tag,
                    $"INFERRED-{index}",
                    $"推断标签 {index}"),
                20 - index,
                null,
                []))
            .Append(new RecommendationFeatureProfile(
                new RecommendationFeature(
                    RecommendationFeatureKind.Tag,
                    "百合",
                    "百合"),
                2.5,
                null,
                [],
                IsSavedTag: true))
            .ToArray();

        var candidates = await provider.GetCandidatesAsync(
            profile,
            new HashSet<int>(),
            CancellationToken.None);

        Assert.Contains(candidates, candidate => candidate.Features.Any(
            feature => feature.Kind == RecommendationFeatureKind.Tag
                && feature.Key == "百合"));
    }

    private sealed class ConcurrentFeatureDataSource : IAnimeDataSource
    {
        private int _activeRequests;
        private int _maximumConcurrency;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<List<Tag>> GetTagsAsync(
            int animeID,
            CancellationToken ct)
        {
            await SimulateRequestAsync(ct);
            return [new Tag("科幻")];
        }

        public async Task<List<Studio>> GetStudioAsync(
            int animeID,
            CancellationToken ct)
        {
            await SimulateRequestAsync(ct);
            return [new Studio(1, "测试制作方", null)];
        }

        public async Task<List<VoiceActor>> GetCVsAsync(
            int animeID,
            CancellationToken ct)
        {
            await SimulateRequestAsync(ct);
            return [new VoiceActor(2, "测试声优", null)];
        }

        private async Task SimulateRequestAsync(CancellationToken token)
        {
            var current = Interlocked.Increment(ref _activeRequests);
            while (true)
            {
                var observed = Volatile.Read(ref _maximumConcurrency);
                if (current <= observed
                    || Interlocked.CompareExchange(
                        ref _maximumConcurrency,
                        current,
                        observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(10, token);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        public Task<List<Anime>> GetAnimeBySeasonAsync(
            int year,
            Season season,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Anime?> GetAnimeDetailAsync(
            int animeID,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<CharacterRole>> GetCharacterRolesAsync(
            int animeID,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(
            string tag,
            int offset,
            string sort,
            CancellationToken ct,
            string? airDateFrom = null,
            string? airDateTo = null)
            => throw new NotSupportedException();

        public Task<List<PersonWork>> GetPersonWorksAsync(
            int personId,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(
            string keyword,
            int offset,
            CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class TagCandidateDataSource : IAnimeDataSource
    {
        public System.Collections.Concurrent.ConcurrentBag<string>
            SearchedTags { get; } = [];

        private static readonly Anime Candidate = new(
            100,
            "科幻候选",
            null,
            [],
            new DateOnly(2026, 1, 1),
            null,
            string.Empty,
            2026,
            1,
            Score: 8);

        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(
            string tag,
            int offset,
            string sort,
            CancellationToken ct,
            string? airDateFrom = null,
            string? airDateTo = null)
        {
            SearchedTags.Add(tag);
            return Task.FromResult((new List<Anime> { Candidate }, 1));
        }

        public Task<Anime?> GetAnimeDetailAsync(
            int animeID,
            CancellationToken ct)
            => Task.FromResult<Anime?>(Candidate);

        public Task<List<Tag>> GetTagsAsync(
            int animeID,
            CancellationToken ct)
            => Task.FromResult(new List<Tag>());

        public Task<List<Studio>> GetStudioAsync(
            int animeID,
            CancellationToken ct)
            => Task.FromResult(new List<Studio>());

        public Task<List<VoiceActor>> GetCVsAsync(
            int animeID,
            CancellationToken ct)
            => Task.FromResult(new List<VoiceActor>());

        public Task<List<Anime>> GetAnimeBySeasonAsync(
            int year,
            Season season,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<CharacterRole>> GetCharacterRolesAsync(
            int animeID,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<PersonWork>> GetPersonWorksAsync(
            int personId,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(
            string keyword,
            int offset,
            CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class DistinctTagCandidateDataSource : IAnimeDataSource
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<
            string,
            int> _tagIds = new(StringComparer.OrdinalIgnoreCase);
        private int _nextTagId;

        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(
            string tag,
            int offset,
            string sort,
            CancellationToken ct,
            string? airDateFrom = null,
            string? airDateTo = null)
        {
            var tagId = _tagIds.GetOrAdd(
                tag,
                _ => Interlocked.Increment(ref _nextTagId));
            var anime = Enumerable.Range(1, 10)
                .Select(index => new Anime(
                    tagId * 100 + index,
                    $"{tag}候选 {index}",
                    null,
                    [],
                    new DateOnly(2026, 1, 1),
                    null,
                    string.Empty,
                    2026,
                    1,
                    Score: 8))
                .ToList();
            return Task.FromResult((anime, anime.Count));
        }

        public Task<Anime?> GetAnimeDetailAsync(
            int animeID,
            CancellationToken ct)
            => Task.FromResult<Anime?>(new Anime(
                animeID,
                $"候选 {animeID}",
                null,
                [],
                new DateOnly(2026, 1, 1),
                null,
                string.Empty,
                2026,
                1,
                Score: 8));

        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => Task.FromResult(new List<Tag>());

        public Task<List<Studio>> GetStudioAsync(
            int animeID,
            CancellationToken ct)
            => Task.FromResult(new List<Studio>());

        public Task<List<VoiceActor>> GetCVsAsync(
            int animeID,
            CancellationToken ct)
            => Task.FromResult(new List<VoiceActor>());

        public Task<List<Anime>> GetAnimeBySeasonAsync(
            int year,
            Season season,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<CharacterRole>> GetCharacterRolesAsync(
            int animeID,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<PersonWork>> GetPersonWorksAsync(
            int personId,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(
            string keyword,
            int offset,
            CancellationToken ct)
            => throw new NotSupportedException();
    }
}
