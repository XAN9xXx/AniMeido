using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AniMeido.Plugin.Base.Services;

public sealed class RecommendationCandidateProvider : IAsyncDisposable
{
    private const int MaximumCandidates = 120;
    private const int EnrichedCandidateCount = 30;
    private readonly IAnimeDataSource _dataSource;
    private readonly ILogger<RecommendationCandidateProvider> _logger;
    private readonly SemaphoreSlim _networkGate = new(4);
    private readonly ServiceOperationLifetime _lifetime = new();

    public RecommendationCandidateProvider(
        IAnimeDataSource dataSource,
        ILogger<RecommendationCandidateProvider> logger)
    {
        // 发现型推荐使用公共作品身份；个人记录适配器为旧记录保留原 ID。
        _dataSource = dataSource is PersonalAnimeDataSource personal ? personal.RecommendationSource : dataSource;
        _logger = logger;
    }

    internal async Task<IReadOnlyDictionary<
        int,
        IReadOnlyList<RecommendationFeature>>> GetFeaturesAsync(
            IReadOnlyList<RecommendationSeed> seeds,
            CancellationToken cancellationToken)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
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

    internal HashSet<int> ExpandKnownExcludedIds(IEnumerable<int> ids)
        => _dataSource is MetadataDataSource metadata ? metadata.ExpandKnownMergedIds(ids) : ids.ToHashSet();

    internal async Task<IReadOnlyList<RecommendationSeed>> ResolveTitlesAsync(
        IReadOnlyList<RecommendationSeed> seeds,
        CancellationToken cancellationToken)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
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
                    Title = detail?.Title ?? AnimeTitleFallback.Untitled,
                };
            });
        return seeds.Select(seed => result[seed.AnimeId]).ToArray();
    }

    // A round owns its remote cursors and unshown candidates. It is process-local;
    // the saved snapshot only needs to preserve the items already shown to the user.
    internal CandidateRound CreateRound(
        IReadOnlyList<RecommendationFeatureProfile> profile,
        IReadOnlySet<int> excludedIds,
        DateOnly? today = null)
    {
        var sources = profile
            .Where(item => item.IsActiveForRecommendation)
            .GroupBy(item => item.Feature.Kind)
            .SelectMany(group => group
                .OrderByDescending(GetExplicitPreferencePriority)
                .ThenByDescending(item => item.EffectiveScore)
                .ThenBy(item => item.Feature.Key, StringComparer.OrdinalIgnoreCase)
                .Take(group.Key switch
                {
                    RecommendationFeatureKind.Tag => 6,
                    RecommendationFeatureKind.Studio => 3,
                    RecommendationFeatureKind.VoiceActor => 5,
                    _ => 0,
                }))
            .ToArray();
        var roundDate = today ?? DateOnly.FromDateTime(DateTime.Today);
        var recentFrom = roundDate.AddYears(-3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var cursors = sources.SelectMany(source => source.Feature.Kind
            == RecommendationFeatureKind.Tag
                ? new[]
                {
                    new CandidateCursor(source, recentFrom, null),
                    new CandidateCursor(source, null, recentFrom),
                }
                : [new CandidateCursor(source, null, null)]).ToArray();
        return new CandidateRound(sources, cursors, excludedIds, roundDate,
            profile.Where(item => item.EffectiveScore < 0).Select(item => item.Feature.Kind).Distinct().ToArray());
    }

    internal async Task<IReadOnlyList<RecommendationCandidate>> GetNextWindowAsync(
        CandidateRound round,
        int count,
        CancellationToken cancellationToken)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
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
            throw new HttpRequestException("推荐的作品没能加载出来，请重试。");

        var enriched = new ConcurrentBag<RecommendationCandidate>();
        var exhausted = new ConcurrentBag<int>();
        await Parallel.ForEachAsync(selected,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 },
            async (candidate, token) =>
            {
                var state = round.Enriched.GetOrAdd(candidate.Anime.ID, _ => new CandidateEnrichment());
                await EnrichAsync(candidate.Anime.ID, state, Enum.GetValues<RecommendationFeatureKind>(), true, token);
                if (state.Anime is not { ID: > 0 } anime)
                {
                    if (state.DetailCompleted || state.DetailAttempts >= 2) exhausted.Add(candidate.Anime.ID);
                    return;
                }
                if (round.NegativeKinds.Any(kind => state.Categories[kind].Features is null))
                {
                    if (round.NegativeKinds.Any(kind => state.Categories[kind] is { Features: null, Attempts: >= 2 }))
                        exhausted.Add(candidate.Anime.ID);
                    return;
                }
                if (round.ExcludedIds.Contains(anime.ID))
                {
                    exhausted.Add(candidate.Anime.ID);
                    return;
                }
                if (candidate.SourceFeatures.Any(feature => feature.Kind != RecommendationFeatureKind.Tag
                    && state.Categories[feature.Kind] is { Features: null, Attempts: < 2 })) return;
                var features = state.Categories.Values.SelectMany(category => category.Features ?? [])
                    .Concat(candidate.SourceFeatures.Where(feature => feature.Kind == RecommendationFeatureKind.Tag))
                    .DistinctBy(feature => (feature.Kind, feature.Key)).ToArray();
                enriched.Add(new RecommendationCandidate(anime, features));
            });
        round.Exclude(ExpandKnownExcludedIds(round.ExcludedIds.Concat(exhausted)));
        // 缓存只属于当前轮次；已展示项目随 Consume 释放，额外保留最多 120 部。
        var selectedIds = selected.Select(item => item.Anime.ID).ToHashSet();
        foreach (var id in round.Enriched.Keys.Where(id => !selectedIds.Contains(id)).Order()
            .Take(Math.Max(0, round.Enriched.Count - MaximumCandidates)))
            round.Enriched.TryRemove(id, out _);
        return enriched.Where(item => !round.ExcludedIds.Contains(item.Anime.ID))
            .GroupBy(item => item.Anime.ID).OrderBy(group => group.Key)
            .Select(group => new RecommendationCandidate(group.First().Anime,
                group.SelectMany(item => item.Features).DistinctBy(feature => (feature.Kind, feature.Key)).ToArray())).ToArray();
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
                        var (items, total, nextOffset) = await WithNetworkGateAsync(ct => ReadTagPageAsync(cursor, ct), token);
                        foreach (var item in items)
                            round.Add(item, cursor.Source);
                        cursor.Exhausted = nextOffset <= cursor.Offset || nextOffset >= total;
                        cursor.Offset = nextOffset;
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
                    if (!token.IsCancellationRequested && ex is not MetadataServiceException { IsRateLimited: true })
                        _logger.LogWarning(ex, "Recommendation source {Kind}:{Key} failed.",
                            cursor.Source.Feature.Kind, cursor.Source.Feature.Key);
                }
            });
        return succeeded > 0;
    }

    private async Task<(List<Anime> Results, int Total, int NextOffset)> ReadTagPageAsync(CandidateCursor cursor, CancellationToken token)
    {
        if (_dataSource is MetadataDataSource metadata)
            return await metadata.SearchTagPageAsync(cursor.Source.Feature.DisplayName, cursor.Offset, "rank", token, cursor.From, cursor.To);
        var (items, total) = await _dataSource.SearchByTagAsync(cursor.Source.Feature.DisplayName, cursor.Offset, "rank", token, cursor.From, cursor.To);
        return (items, total, cursor.Offset + items.Count);
    }

    internal sealed class CandidateRound(
        IReadOnlyList<RecommendationFeatureProfile> sources,
        CandidateCursor[] cursors,
        IReadOnlySet<int> excludedIds,
        DateOnly today,
        IReadOnlyList<RecommendationFeatureKind> negativeKinds)
    {
        internal DateOnly Today { get; } = today;
        internal IReadOnlyList<RecommendationFeatureKind> NegativeKinds { get; } = negativeKinds;
        internal ConcurrentDictionary<int, CandidateEnrichment> Enriched { get; } = new();
        internal IReadOnlyList<RecommendationFeatureProfile> Sources { get; } = sources;
        internal List<CandidateCursor> Cursors { get; } = [.. cursors];
        internal ConcurrentDictionary<int, CandidateSource> Pending { get; } = new();
        internal HashSet<int> ExcludedIds { get; } = [.. excludedIds];
        private readonly ConcurrentDictionary<(int, RecommendationFeatureKind, string), byte> _seen = new();
        internal bool InitialFetchComplete { get; set; }
        internal bool HasMore => !Pending.IsEmpty || Cursors.Any(cursor => !cursor.Exhausted);

        internal bool HasMoreAfterConsuming(IEnumerable<int> ids)
        {
            var consumed = SourceIds(ids).ToHashSet();
            return Pending.Keys.Any(id => !consumed.Contains(id)) || Cursors.Any(cursor => !cursor.Exhausted);
        }

        private int[] SourceIds(IEnumerable<int> ids)
        {
            var targets = ids.ToHashSet();
            foreach (var id in targets.ToArray())
                if (Enriched.TryGetValue(id, out var state) && state.Anime is { } anime) targets.Add(anime.ID);
            return targets.Concat(Enriched.Where(pair => pair.Value.Anime is { } anime && targets.Contains(anime.ID))
                .Select(pair => pair.Key)).Distinct().ToArray();
        }

        internal void Exclude(IEnumerable<int> ids)
        {
            foreach (var id in SourceIds(ids))
            {
                ExcludedIds.Add(id);
                Pending.TryRemove(id, out _);
                Enriched.TryRemove(id, out _);
            }
        }

        internal void Consume(IEnumerable<int> ids)
            => Exclude(ids);

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
        CancellationToken cancellationToken,
        IReadOnlyList<RecommendationFeatureProfile>? profile = null)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
        var (year, season) = SeasonHelper.GetCurrentSeason();
        var anime = await WithNetworkGateAsync(
            ct => _dataSource.GetAnimeBySeasonAsync(year, season, ct),
            cancellationToken);
        var ordered = anime
            .Where(item => !excludedIds.Contains(item.ID))
            .OrderByDescending(item => item.Score ?? 0)
            .ThenBy(item => item.ID).ToArray();
        var negativeProfile = (profile ?? []).Where(item => item.EffectiveScore < 0).ToArray();
        if (negativeProfile.Length > 0)
        {
            var kinds = negativeProfile.Select(item => item.Feature.Kind).Distinct().ToArray();
            var candidates = new ConcurrentBag<RecommendationCandidate>();
            await Parallel.ForEachAsync(ordered.Take(EnrichedCandidateCount),
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 },
                async (item, token) =>
                {
                    var state = new CandidateEnrichment();
                    await EnrichAsync(item.ID, state, kinds, false, token);
                    if (kinds.All(kind => state.Categories[kind].Features is not null))
                        candidates.Add(new(item, state.Categories.Values.SelectMany(category => category.Features ?? []).ToArray()));
                });
            return RecommendationScorer.Rank(negativeProfile, candidates.ToArray(), DateOnly.FromDateTime(DateTime.Today),
                requirePositiveMatch: false).Select(item => item with
                { Reasons = item.Reasons.Prepend(PopularReason()).ToArray() }).ToArray();
        }
        return ordered.Take(20)
            .Select(item => new RecommendationItem(
                item,
                item.Score ?? 0,
                [PopularReason()],
                false,
                true))
            .ToArray();
    }

    private static RecommendationReason PopularReason() => new(
        new(RecommendationFeatureKind.Tag, "popular", "本季热门"), 0, false, "本季推荐");

    internal sealed class CandidateEnrichment
    {
        internal Anime? Anime { get; set; }
        internal bool DetailCompleted { get; set; }
        internal int DetailAttempts { get; set; }
        internal Dictionary<RecommendationFeatureKind, FeatureRead> Categories { get; } = new();
    }

    internal sealed record FeatureRead(IReadOnlyList<RecommendationFeature>? Features, int Attempts);

    private async Task EnrichAsync(int animeId, CandidateEnrichment state,
        IReadOnlyList<RecommendationFeatureKind> kinds, bool includeDetail, CancellationToken token)
    {
        if (includeDetail && !state.DetailCompleted && state.DetailAttempts < 2)
        {
            try
            {
                state.Anime = await WithNetworkGateAsync(ct => _dataSource.GetAnimeDetailAsync(animeId, ct), token);
                state.DetailCompleted = true;
                state.DetailAttempts++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.DetailAttempts++;
                LogEnrichmentFailure(ex, token);
            }
        }
        // 各类别保留成功空集合；只有失败类别下一次读取时重试一次。
        foreach (var kind in kinds)
        {
            state.Categories.TryGetValue(kind, out var previous);
            if (previous?.Features is not null || previous?.Attempts >= 2) continue;
            IReadOnlyList<RecommendationFeature>? features;
            try
            {
                features = kind switch
                {
                    RecommendationFeatureKind.Tag => (await WithNetworkGateAsync(ct => _dataSource.GetTagsAsync(animeId, ct), token))
                        .Select(tag => new RecommendationFeature(kind, NormalizeTag(tag.Name), tag.Name.Trim())).ToArray(),
                    RecommendationFeatureKind.Studio => (await WithNetworkGateAsync(ct => _dataSource.GetStudioAsync(animeId, ct), token))
                        .Select(studio => new RecommendationFeature(kind, studio.ID.ToString(System.Globalization.CultureInfo.InvariantCulture), studio.Name)).ToArray(),
                    RecommendationFeatureKind.VoiceActor => (await WithNetworkGateAsync(ct => _dataSource.GetCVsAsync(animeId, ct), token))
                        .Select(actor => new RecommendationFeature(kind, actor.VoiceActorId.ToString(System.Globalization.CultureInfo.InvariantCulture), actor.Name)).ToArray(),
                    _ => throw new ArgumentOutOfRangeException(nameof(kinds)),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                features = null;
                LogEnrichmentFailure(ex, token);
            }
            state.Categories[kind] = new(features, (previous?.Attempts ?? 0) + 1);
        }
    }

    private void LogEnrichmentFailure(Exception exception, CancellationToken token)
    {
        if (!token.IsCancellationRequested && exception is not MetadataServiceException { IsRateLimited: true })
            _logger.LogWarning(exception, "Recommendation enrichment failed; retaining category state for the next request.");
    }

    internal async Task<IReadOnlyList<RecommendationFeature>> GetTagsForPreviewAsync(
        int animeId, CancellationToken cancellationToken)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
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
            if (!cancellationToken.IsCancellationRequested && ex is not MetadataServiceException { IsRateLimited: true })
                _logger.LogWarning(ex, "Recommendation feature enrichment failed; continuing with available features.");
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
            if (!cancellationToken.IsCancellationRequested && ex is not MetadataServiceException { IsRateLimited: true })
                _logger.LogWarning(ex,
                    "Recommendation detail enrichment failed for {AnimeId}; retaining the candidate.", animeId);
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

    public ValueTask DisposeAsync() => _lifetime.StopAsync(_networkGate.Dispose);

    internal sealed record CandidateSource(
        Anime Anime,
        double SourceScore,
        IReadOnlyList<RecommendationFeature> SourceFeatures);
}
