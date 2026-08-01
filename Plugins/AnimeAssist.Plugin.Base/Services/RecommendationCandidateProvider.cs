using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AniMeido.Plugin.Base.Services;

public sealed class RecommendationCandidateProvider : IDisposable
{
    private const int MaximumCandidates = 120;
    private const int EnrichedCandidateCount = 30;
    private readonly IAnimeDataSource _dataSource;
    private readonly ILogger<RecommendationCandidateProvider> _logger;
    private readonly SemaphoreSlim _networkGate = new(4);
    private bool _disposed;

    public RecommendationCandidateProvider(
        IAnimeDataSource dataSource,
        ILogger<RecommendationCandidateProvider> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    internal async Task<IReadOnlyDictionary<
        int,
        IReadOnlyList<RecommendationFeature>>> GetFeaturesAsync(
            IReadOnlyList<RecommendationSeed> seeds,
            CancellationToken cancellationToken)
    {
        var result = new ConcurrentDictionary<
            int,
            IReadOnlyList<RecommendationFeature>>();
        await Parallel.ForEachAsync(
            seeds,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4,
            },
            async (seed, token) =>
            {
                result[seed.AnimeId] = await GetAnimeFeaturesAsync(
                    seed.AnimeId,
                    token);
            });
        return result;
    }

    internal async Task<IReadOnlyList<RecommendationSeed>> ResolveTitlesAsync(
        IReadOnlyList<RecommendationSeed> seeds,
        CancellationToken cancellationToken)
    {
        var result = new ConcurrentDictionary<int, RecommendationSeed>();
        await Parallel.ForEachAsync(
            seeds,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4,
            },
            async (seed, token) =>
            {
                if (!string.IsNullOrWhiteSpace(seed.Title))
                {
                    result[seed.AnimeId] = seed;
                    return;
                }

                var detail = await TryGetDetailAsync(seed.AnimeId, token);
                result[seed.AnimeId] = seed with
                {
                    Title = detail?.Title ?? $"Bangumi #{seed.AnimeId}",
                };
            });
        return seeds.Select(seed => result[seed.AnimeId]).ToArray();
    }

    internal async Task<IReadOnlyList<RecommendationCandidate>> GetCandidatesAsync(
        IReadOnlyList<RecommendationFeatureProfile> profile,
        IReadOnlySet<int> excludedIds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sources = profile
            .Where(item => item.EffectiveScore > 0)
            .GroupBy(item => item.Feature.Kind)
            .SelectMany(group => group
                .OrderByDescending(GetExplicitPreferencePriority)
                .ThenByDescending(item => item.EffectiveScore)
                .Take(group.Key switch
                {
                    RecommendationFeatureKind.Tag => 6,
                    RecommendationFeatureKind.Studio => 3,
                    RecommendationFeatureKind.VoiceActor => 5,
                    _ => 0,
                }))
            .ToArray();
        var recentFrom = $"{DateTime.UtcNow.Year - 3:D4}-01-01";
        var raw = new ConcurrentDictionary<int, CandidateSource>();
        var successfulSources = 0;
        await Parallel.ForEachAsync(
            sources,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4,
            },
            async (source, token) =>
            {
                try
                {
                    if (source.Feature.Kind == RecommendationFeatureKind.Tag)
                    {
                        var recent = await WithNetworkGateAsync(
                            ct => _dataSource.SearchByTagAsync(
                                source.Feature.DisplayName,
                                0,
                                "rank",
                                ct,
                                recentFrom,
                                null),
                            token);
                        AddCandidates(
                            raw,
                            recent.Results,
                            source.Feature,
                            source.EffectiveScore,
                            excludedIds);
                        var classic = await WithNetworkGateAsync(
                            ct => _dataSource.SearchByTagAsync(
                                source.Feature.DisplayName,
                                0,
                                "rank",
                                ct,
                                null,
                                recentFrom),
                            token);
                        AddCandidates(
                            raw,
                            classic.Results,
                            source.Feature,
                            source.EffectiveScore,
                            excludedIds);
                        Interlocked.Increment(ref successfulSources);
                        return;
                    }

                    if (!int.TryParse(source.Feature.Key, out var personId))
                    {
                        return;
                    }

                    var works = await WithNetworkGateAsync(
                        ct => _dataSource.GetPersonWorksAsync(personId, ct),
                        token);
                    foreach (var work in works)
                    {
                        if (excludedIds.Contains(work.ID))
                        {
                            continue;
                        }

                        var placeholder = new Anime(
                            work.ID,
                            work.Title,
                            null,
                            [],
                            null,
                            work.CoverURL,
                            string.Empty,
                            0,
                            0);
                        raw.AddOrUpdate(
                            work.ID,
                            _ => new CandidateSource(
                                placeholder,
                                source.EffectiveScore,
                                [source.Feature]),
                            (_, existing) => existing with
                            {
                                SourceScore = existing.SourceScore
                                    + source.EffectiveScore,
                                SourceFeatures = MergeSourceFeatures(
                                    existing.SourceFeatures,
                                    source.Feature),
                            });
                    }

                    Interlocked.Increment(ref successfulSources);
                }
#pragma warning disable CA1031 // One remote feature source must not abort the remaining recommendation refresh.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Recommendation source {Kind}:{Key} failed.",
                        source.Feature.Kind,
                        source.Feature.Key);
                }
#pragma warning restore CA1031
            });

        if (sources.Length > 0 && successfulSources == 0)
        {
            throw new HttpRequestException(
                "所有推荐候选来源均请求失败。");
        }

        var provisional = raw.Values
            .OrderByDescending(item => item.SourceScore)
            .ThenByDescending(item => item.Anime.Score ?? 0)
            .Take(MaximumCandidates)
            .ToArray();
        var enrichmentCandidates = SelectEnrichmentCandidates(
            provisional,
            raw.Values,
            sources);
        var enriched = new ConcurrentBag<RecommendationCandidate>();
        await Parallel.ForEachAsync(
            enrichmentCandidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4,
            },
            async (candidate, token) =>
            {
                var anime = await TryGetDetailAsync(
                    candidate.Anime.ID,
                    token) ?? candidate.Anime;
                var features = (await GetAnimeFeaturesAsync(anime.ID, token))
                    .Concat(candidate.SourceFeatures)
                    .DistinctBy(feature => (feature.Kind, feature.Key))
                    .ToArray();
                enriched.Add(new RecommendationCandidate(
                    anime,
                    features,
                    candidate.SourceScore * 0.05));
            });
        return enriched.ToArray();
    }

    private static IReadOnlyList<CandidateSource> SelectEnrichmentCandidates(
        IReadOnlyList<CandidateSource> provisional,
        IEnumerable<CandidateSource> allCandidates,
        IReadOnlyList<RecommendationFeatureProfile> sources)
    {
        var selected = new List<CandidateSource>(EnrichedCandidateCount);
        var selectedIds = new HashSet<int>();
        foreach (var source in sources
            .Where(item => GetExplicitPreferencePriority(item) > 0)
            .OrderByDescending(GetExplicitPreferencePriority)
            .ThenByDescending(item => item.EffectiveScore))
        {
            foreach (var candidate in allCandidates
                .Where(item => item.SourceFeatures.Any(feature =>
                    FeatureEquals(feature, source.Feature)))
                .OrderByDescending(item => item.SourceScore)
                .ThenByDescending(item => item.Anime.Score ?? 0)
                .Take(3))
            {
                if (selectedIds.Add(candidate.Anime.ID))
                {
                    selected.Add(candidate);
                }

                if (selected.Count == EnrichedCandidateCount)
                {
                    return selected;
                }
            }
        }

        foreach (var candidate in provisional)
        {
            if (selectedIds.Add(candidate.Anime.ID))
            {
                selected.Add(candidate);
            }

            if (selected.Count == EnrichedCandidateCount)
            {
                break;
            }
        }

        return selected;
    }

    internal async Task<IReadOnlyList<RecommendationItem>> GetPopularAsync(
        IReadOnlySet<int> excludedIds,
        CancellationToken cancellationToken)
    {
        var (year, season) = SeasonHelper.GetCurrentSeason();
        var anime = await WithNetworkGateAsync(
            ct => _dataSource.GetAnimeBySeasonAsync(year, season, ct),
            cancellationToken);
        return anime
            .Where(item => !excludedIds.Contains(item.ID))
            .OrderByDescending(item => item.Score ?? 0)
            .Take(20)
            .Select(item => new RecommendationItem(
                item,
                item.Score ?? 0,
                [new RecommendationReason(
                    new RecommendationFeature(
                        RecommendationFeatureKind.Tag,
                        "popular",
                        "本季热门"),
                    0,
                    false,
                    "热门推荐，尚未个性化")],
                false,
                true))
            .ToArray();
    }

    private async Task<IReadOnlyList<RecommendationFeature>>
        GetAnimeFeaturesAsync(
            int animeId,
            CancellationToken cancellationToken)
    {
        var tagsTask = TryGetAsync(
            ct => _dataSource.GetTagsAsync(animeId, ct),
            cancellationToken);
        var studiosTask = TryGetAsync(
            ct => _dataSource.GetStudioAsync(animeId, ct),
            cancellationToken);
        var actorsTask = TryGetAsync(
            ct => _dataSource.GetCVsAsync(animeId, ct),
            cancellationToken);
        await Task.WhenAll(tagsTask, studiosTask, actorsTask);
        return tagsTask.Result
            .Select(tag => new RecommendationFeature(
                RecommendationFeatureKind.Tag,
                NormalizeTag(tag.Name),
                tag.Name.Trim()))
            .Concat(studiosTask.Result.Select(studio =>
                new RecommendationFeature(
                    RecommendationFeatureKind.Studio,
                    studio.ID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    studio.Name)))
            .Concat(actorsTask.Result.Select(actor =>
                new RecommendationFeature(
                    RecommendationFeatureKind.VoiceActor,
                    actor.VoiceActorId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    actor.Name)))
            .DistinctBy(item => (item.Kind, item.Key))
            .ToArray();
    }

    private async Task<IReadOnlyList<T>> TryGetAsync<T>(
        Func<CancellationToken, Task<List<T>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WithNetworkGateAsync(operation, cancellationToken);
        }
#pragma warning disable CA1031 // Missing one feature category is an expected partial remote failure.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Recommendation feature enrichment failed.");
            return [];
        }
#pragma warning restore CA1031
    }

    private async Task<Anime?> TryGetDetailAsync(
        int animeId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WithNetworkGateAsync(
                ct => _dataSource.GetAnimeDetailAsync(animeId, ct),
                cancellationToken);
        }
#pragma warning disable CA1031 // A placeholder candidate can survive a failed detail enrichment.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "Recommendation detail enrichment failed for {AnimeId}.",
                animeId);
            return null;
        }
#pragma warning restore CA1031
    }

    private async Task<T> WithNetworkGateAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _networkGate.WaitAsync(cancellationToken);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            _networkGate.Release();
        }
    }

    private static void AddCandidates(
        ConcurrentDictionary<int, CandidateSource> target,
        IEnumerable<Anime> anime,
        RecommendationFeature sourceFeature,
        double sourceScore,
        IReadOnlySet<int> excludedIds)
    {
        foreach (var item in anime)
        {
            if (excludedIds.Contains(item.ID))
            {
                continue;
            }

            target.AddOrUpdate(
                item.ID,
                _ => new CandidateSource(item, sourceScore, [sourceFeature]),
                (_, existing) => new CandidateSource(
                    PreferComplete(item, existing.Anime),
                    existing.SourceScore + sourceScore,
                    MergeSourceFeatures(
                        existing.SourceFeatures,
                        sourceFeature)));
        }
    }

    private static IReadOnlyList<RecommendationFeature> MergeSourceFeatures(
        IReadOnlyList<RecommendationFeature> existing,
        RecommendationFeature added)
        => existing
            .Append(added)
            .DistinctBy(feature => (feature.Kind, feature.Key))
            .ToArray();

    private static int GetExplicitPreferencePriority(
        RecommendationFeatureProfile profile)
        => profile.Adjustment == RecommendationAdjustment.Like
            ? 2
            : profile.IsSavedTag ? 1 : 0;

    private static bool FeatureEquals(
        RecommendationFeature first,
        RecommendationFeature second)
        => first.Kind == second.Kind
            && string.Equals(
                first.Key,
                second.Key,
                StringComparison.OrdinalIgnoreCase);

    private static Anime PreferComplete(Anime first, Anime second)
        => first.AirDate is not null || second.AirDate is null
            ? first
            : second;

    internal static string NormalizeTag(string value)
        => value.Trim().Normalize().ToUpperInvariant();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _networkGate.Dispose();
    }

    private sealed record CandidateSource(
        Anime Anime,
        double SourceScore,
        IReadOnlyList<RecommendationFeature> SourceFeatures);
}
