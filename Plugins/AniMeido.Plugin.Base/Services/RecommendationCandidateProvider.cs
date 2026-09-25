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

    // A round owns its remote cursors and unshown candidates. It is process-local;
    // the saved snapshot only needs to preserve the items already shown to the user.
    internal CandidateRound CreateRound(
        IReadOnlyList<RecommendationFeatureProfile> profile,
        IReadOnlySet<int> excludedIds)
    {
        var sources = profile
            .Where(item => item.IsActiveForRecommendation)
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
        var cursors = sources.SelectMany(source => source.Feature.Kind
            == RecommendationFeatureKind.Tag
                ? new[]
                {
                    new CandidateCursor(source, recentFrom, null),
                    new CandidateCursor(source, null, recentFrom),
                }
                : [new CandidateCursor(source, null, null)]).ToArray();
        return new CandidateRound(sources, cursors, excludedIds);
    }

    internal async Task<IReadOnlyList<RecommendationCandidate>> GetNextWindowAsync(
        CandidateRound round,
        int count,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var failedToProgress = false;
        // Fetch every source's first page before ranking the first window, so
        // source order cannot decide which preference gets represented.
        if (!round.InitialFetchComplete)
        {
            await FetchCursorsAsync(round, round.Cursors, cancellationToken);
            round.InitialFetchComplete = true;
        }

        var pageBudget = 8;
        while (round.Pending.Count < count && pageBudget > 0
            && round.Cursors.Any(cursor => !cursor.Exhausted))
        {
            var cursors = round.Cursors.Where(cursor => !cursor.Exhausted)
                .Take(Math.Min(4, pageBudget)).ToArray();
            pageBudget -= cursors.Length;
            var progressed = await FetchCursorsAsync(round, cursors, cancellationToken);
            if (!progressed)
            {
                failedToProgress = true;
                break; // Failed sources remain retryable on the next request.
            }
            // Rotate the cursors rather than draining the strongest tag first.
            foreach (var cursor in cursors)
            {
                round.Cursors.Remove(cursor);
                round.Cursors.Add(cursor);
            }
        }

        var pending = round.Pending.Values
            .Where(item => !round.ExcludedIds.Contains(item.Anime.ID))
            .OrderByDescending(item => item.SourceScore)
            .ThenByDescending(item => item.Anime.Score ?? 0)
            .ThenBy(item => item.Anime.ID)
            .Take(MaximumCandidates)
            .ToArray();
        var selected = SelectEnrichmentCandidates(pending, round.Pending.Values
            .Where(item => !round.ExcludedIds.Contains(item.Anime.ID)), round.Sources)
            .Take(count).ToArray();
        if (selected.Length == 0 && failedToProgress)
            throw new HttpRequestException("推荐候选暂时无法加载，请重试。");

        var enriched = new ConcurrentBag<RecommendationCandidate>();
        await Parallel.ForEachAsync(selected,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 },
            async (candidate, token) =>
            {
                var anime = await TryGetDetailAsync(candidate.Anime.ID, token) ?? candidate.Anime;
                var features = (await GetAnimeFeaturesAsync(anime.ID, token))
                    .Concat(candidate.SourceFeatures)
                    .DistinctBy(feature => (feature.Kind, feature.Key)).ToArray();
                enriched.Add(new RecommendationCandidate(anime, features));
            });
        return enriched.OrderBy(item => item.Anime.ID).ToArray();
    }

    private async Task<bool> FetchCursorsAsync(CandidateRound round,
        IReadOnlyList<CandidateCursor> cursors, CancellationToken cancellationToken)
    {
        var succeeded = 0;
        await Parallel.ForEachAsync(cursors,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 },
            async (cursor, token) =>
            {
                try
                {
                    if (cursor.Source.Feature.Kind == RecommendationFeatureKind.Tag)
                    {
                        var (items, total) = await WithNetworkGateAsync(ct =>
                            _dataSource.SearchByTagAsync(cursor.Source.Feature.DisplayName,
                                cursor.Offset, "rank", ct, cursor.From, cursor.To), token);
                        foreach (var item in items)
                            round.Add(item, cursor.Source);
                        cursor.Offset += items.Count;
                        cursor.Exhausted = items.Count == 0 || cursor.Offset >= total;
                    }
                    else if (int.TryParse(cursor.Source.Feature.Key, out var personId))
                    {
                        var works = await WithNetworkGateAsync(
                            ct => _dataSource.GetPersonWorksAsync(personId, ct), token);
                        foreach (var work in works)
                            round.Add(new Anime(work.ID, work.Title, null, [], null,
                                work.CoverURL, string.Empty, 0, 0), cursor.Source);
                        cursor.Exhausted = true;
                    }
                    else cursor.Exhausted = true;
                    Interlocked.Increment(ref succeeded);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Recommendation source {Kind}:{Key} failed.",
                        cursor.Source.Feature.Kind, cursor.Source.Feature.Key);
                }
            });
        return succeeded > 0;
    }

    internal sealed class CandidateRound(
        IReadOnlyList<RecommendationFeatureProfile> sources,
        CandidateCursor[] cursors,
        IReadOnlySet<int> excludedIds)
    {
        internal IReadOnlyList<RecommendationFeatureProfile> Sources { get; } = sources;
        internal List<CandidateCursor> Cursors { get; } = [.. cursors];
        internal ConcurrentDictionary<int, CandidateSource> Pending { get; } = new();
        internal HashSet<int> ExcludedIds { get; } = [.. excludedIds];
        private readonly ConcurrentDictionary<(int, RecommendationFeatureKind, string), byte> _seen = new();
        internal bool InitialFetchComplete { get; set; }
        internal bool HasMore => !Pending.IsEmpty || Cursors.Any(cursor => !cursor.Exhausted);

        internal void Exclude(IEnumerable<int> ids)
        {
            foreach (var id in ids)
            {
                ExcludedIds.Add(id);
                Pending.TryRemove(id, out _);
            }
        }

        internal void Consume(IEnumerable<int> ids)
        {
            foreach (var id in ids)
            {
                Pending.TryRemove(id, out _);
                ExcludedIds.Add(id);
            }
        }

        internal void Add(Anime anime, RecommendationFeatureProfile source)
        {
            if (anime.ID <= 0 || ExcludedIds.Contains(anime.ID)
                || !_seen.TryAdd((anime.ID, source.Feature.Kind, source.Feature.Key), 0)) return;
            Pending.AddOrUpdate(anime.ID,
                _ => new CandidateSource(anime, source.EffectiveScore, [source.Feature]),
                (_, existing) => new CandidateSource(PreferComplete(anime, existing.Anime),
                    existing.SourceScore + source.EffectiveScore,
                    MergeSourceFeatures(existing.SourceFeatures, source.Feature)));
        }
    }

    internal sealed class CandidateCursor(
        RecommendationFeatureProfile source, string? from, string? to)
    {
        internal RecommendationFeatureProfile Source { get; } = source;
        internal string? From { get; } = from;
        internal string? To { get; } = to;
        internal int Offset { get; set; }
        internal bool Exhausted { get; set; }
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
                .ThenBy(item => item.Anime.ID)
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

    internal async Task<IReadOnlyList<RecommendationFeature>> GetTagsForPreviewAsync(
        int animeId, CancellationToken cancellationToken)
    {
        // Unlike ranking enrichment, the editor must distinguish failure from an empty tag set.
        var tags = await WithNetworkGateAsync(
            ct => _dataSource.GetTagsAsync(animeId, ct), cancellationToken);
        return tags.Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
            .Select(tag => new RecommendationFeature(RecommendationFeatureKind.Tag,
                NormalizeTag(tag.Name), tag.Name.Trim()))
            .DistinctBy(tag => tag.Key).ToArray();
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

    internal sealed record CandidateSource(
        Anime Anime,
        double SourceScore,
        IReadOnlyList<RecommendationFeature> SourceFeatures);
}
