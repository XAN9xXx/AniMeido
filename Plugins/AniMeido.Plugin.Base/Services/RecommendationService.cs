using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed class RecommendationService : IDisposable
{
    private const string SnapshotCacheKey = "recommendations:snapshot:v1";
    private static readonly TimeSpan SnapshotFreshness = TimeSpan.FromHours(24);
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(90);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly TrackingService _tracking;
    private readonly SavedTagService _savedTags;
    private readonly ArchiveService _archive;
    private readonly BrowseHistoryService _history;
    private readonly ActionCenterService _actionCenter;
    private readonly CacheService _cache;
    private readonly RecommendationCandidateProvider _candidates;
    private readonly SemaphoreSlim _refreshGate = new(1);
    private readonly SemaphoreSlim _snapshotCacheGate = new(1);
    private long _snapshotRevision;
    private RecommendationCandidateProvider.CandidateRound? _round;
    private DateTimeOffset _roundGeneratedAt;
    private bool _disposed;

    public RecommendationService(
        SqliteConnectionFactory dbFactory,
        TrackingService tracking,
        SavedTagService savedTags,
        ArchiveService archive,
        BrowseHistoryService history,
        ActionCenterService actionCenter,
        CacheService cache,
        RecommendationCandidateProvider candidates)
    {
        _dbFactory = dbFactory;
        _tracking = tracking;
        _savedTags = savedTags;
        _archive = archive;
        _history = history;
        _actionCenter = actionCenter;
        _cache = cache;
        _candidates = candidates;
    }

    public Task<IReadOnlyList<RecommendationFeature>> GetPreviewTagsAsync(
        int animeId, CancellationToken cancellationToken = default)
        => _candidates.GetTagsForPreviewAsync(animeId, cancellationToken);

    public Task<AnimeTrackingStatus?> GetTrackingStatusAsync(int animeId)
        => _tracking.GetStatusAsync(animeId);

    public async Task<(AnimeTrackingStatus? Status, bool Changed)> ToggleFollowingAsync(int animeId,
        CancellationToken cancellationToken = default)
    {
        var result = await _tracking.ToggleFollowingAsync(animeId, cancellationToken);
        if (result.Changed) await InvalidateSnapshotAsync();
        return result;
    }

    public IReadOnlyList<RecommendationFeatureProfile> LastProfile
    {
        get;
        private set;
    } = [];

    internal static bool IsSnapshotFresh(RecommendationSnapshot snapshot, DateTimeOffset now)
        => snapshot.SchemaVersion == RecommendationSnapshot.CurrentSchemaVersion
            && snapshot.GeneratedAt >= now - SnapshotFreshness;

    public async Task<RecommendationSnapshot?> GetCachedSnapshotAsync(
        bool allowExpired,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var json = await _cache.GetCacheAllowExpiredAsync(SnapshotCacheKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<RecommendationSnapshot>(
                json,
                JsonOptions);
            if (snapshot?.SchemaVersion
                != RecommendationSnapshot.CurrentSchemaVersion)
            {
                return null;
            }

            return allowExpired
                || IsSnapshotFresh(snapshot, DateTimeOffset.UtcNow)
                    ? snapshot
                    : null;
        }
        catch (JsonException)
        {
            await _cache.RemoveCacheAsync(SnapshotCacheKey);
            return null;
        }
    }

    public async Task<RecommendationGeneration?> RefreshAsync(
        CancellationToken cancellationToken = default,
        bool preferNewBatch = false,
        IReadOnlySet<int>? displayedIds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var snapshotRevision = Interlocked.Read(ref _snapshotRevision);
            var cacheGeneration = _cache.CaptureGeneration();
            var previousSnapshot = preferNewBatch
                ? await GetCachedSnapshotAsync(
                    allowExpired: true,
                    cancellationToken)
                : null;
            var previousIds = preferNewBatch
                ? displayedIds is { Count: > 0 }
                    ? displayedIds.ToHashSet()
                    : previousSnapshot?.Items
                        .Select(item => item.Anime.ID)
                        .ToHashSet()
                : null;
            var tracking = await _tracking.GetAllTrackingAsync();
            var hidden = await GetHiddenAnimeAsync(cancellationToken);
            var excluded = tracking.Select(item => item.AnimeId)
                .Concat(hidden.Select(item => item.AnimeId))
                .ToHashSet();
            if (previousIds is not null)
            {
                excluded.UnionWith(previousIds);
            }
            var preferences = await GetFeaturePreferencesAsync(
                cancellationToken);
            var savedTags = await _savedTags.GetAllSavedTagsAsync();
            var seeds = await BuildSeedsAsync(
                tracking,
                cancellationToken);
            seeds = await _candidates.ResolveTitlesAsync(
                seeds,
                cancellationToken);
            var features = await _candidates.GetFeaturesAsync(
                seeds,
                cancellationToken);
            var profile = RecommendationScorer.BuildProfile(
                seeds,
                features,
                preferences,
                savedTags);

            IReadOnlyList<RecommendationItem> items;
            RecommendationCandidateProvider.CandidateRound? newRound = null;
            var personalized = profile.Any(item => item.IsActiveForRecommendation);
            if (personalized)
            {
                newRound = _candidates.CreateRound(profile, excluded);
                items = await GenerateBatchAsync(newRound, profile,
                    cancellationToken);
                if (items.Count == 0 && !newRound.HasMore)
                {
                    items = await _candidates.GetPopularAsync(
                        excluded,
                        cancellationToken);
                    personalized = false;
                    newRound = null;
                }
            }
            else
            {
                items = await _candidates.GetPopularAsync(
                    excluded,
                    cancellationToken);
            }

            items = items
                .DistinctBy(item => item.Anime.ID)
                .Take(20)
                .ToArray();
            if (preferNewBatch
                && items.Count == 0
                && previousSnapshot is not null
                && newRound?.HasMore != true)
            {
                if (snapshotRevision != Interlocked.Read(ref _snapshotRevision))
                    return null;
                LastProfile = profile;
                return new RecommendationGeneration(
                    previousSnapshot,
                    profile);
            }

            var snapshot = new RecommendationSnapshot(
                RecommendationSnapshot.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                personalized,
                items,
                personalized ? profile : null,
                newRound?.HasMore == true);
            if (!await TryStoreSnapshotAsync(snapshot, cacheGeneration,
                snapshotRevision, cancellationToken))
                return null;
            LastProfile = profile;
            _round = newRound;
            _roundGeneratedAt = snapshot.GeneratedAt;
            return new RecommendationGeneration(snapshot, profile);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<RecommendationPageBatch> LoadMoreAsync(
        RecommendationSnapshot snapshot,
        IReadOnlySet<int> removedIds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var snapshotRevision = Interlocked.Read(ref _snapshotRevision);
            var cacheGeneration = _cache.CaptureGeneration();
            if (!snapshot.IsPersonalized || !snapshot.HasMore)
                return new RecommendationPageBatch(snapshot, []);

            var profile = snapshot.RoundProfile ?? [];
            if (profile.Count == 0)
                return new RecommendationPageBatch(snapshot with { HasMore = false }, []);
            var tracking = await _tracking.GetAllTrackingAsync();
            var hidden = await GetHiddenAnimeAsync(cancellationToken);
            var excluded = tracking.Select(item => item.AnimeId)
                .Concat(hidden.Select(item => item.AnimeId))
                .Concat(removedIds)
                .Concat(snapshot.Items.Select(item => item.Anime.ID))
                .ToHashSet();
            if (_round is null || _roundGeneratedAt != snapshot.GeneratedAt)
            {
                _round = _candidates.CreateRound(profile, excluded);
                _roundGeneratedAt = snapshot.GeneratedAt;
            }
            _round.Exclude(excluded);
            var items = await GenerateBatchAsync(_round, profile, cancellationToken);
            var updated = snapshot with
            {
                Items = snapshot.Items.Concat(items)
                    .DistinctBy(item => item.Anime.ID).ToArray(),
                HasMore = _round.HasMore,
            };
            // An invalidated cache must stay removed, but this page can keep
            // browsing its existing round (including edits to next-round preferences).
            if (snapshotRevision == Interlocked.Read(ref _snapshotRevision))
            {
                var cached = await GetCachedSnapshotAsync(allowExpired: true,
                    cancellationToken);
                if (cached?.GeneratedAt == snapshot.GeneratedAt)
                {
                    await TryStoreSnapshotAsync(updated, cacheGeneration,
                        snapshotRevision, cancellationToken);
                }
            }
            if (snapshotRevision != Interlocked.Read(ref _snapshotRevision))
            {
                var currentTracking = await _tracking.GetAllTrackingAsync();
                var currentHidden = await GetHiddenAnimeAsync(cancellationToken);
                var currentExcluded = currentTracking.Select(item => item.AnimeId)
                    .Concat(currentHidden.Select(item => item.AnimeId))
                    .Concat(removedIds)
                    .ToHashSet();
                _round.Exclude(currentExcluded);
                items = items.Where(item => !currentExcluded.Contains(item.Anime.ID))
                    .ToArray();
                updated = updated with
                {
                    Items = snapshot.Items.Concat(items)
                        .DistinctBy(item => item.Anime.ID).ToArray(),
                    HasMore = _round.HasMore,
                };
            }
            return new RecommendationPageBatch(updated, items);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<IReadOnlyList<RecommendationItem>> GenerateBatchAsync(
        RecommendationCandidateProvider.CandidateRound round,
        IReadOnlyList<RecommendationFeatureProfile> profile,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var window = await _candidates.GetNextWindowAsync(round, 30, cancellationToken);
            if (window.Count == 0)
            {
                if (!round.HasMore) return [];
                continue;
            }
            var ranked = RecommendationScorer.Rank(profile, window,
                DateOnly.FromDateTime(DateTime.Today));
            round.Consume(ranked.Select(item => item.Anime.ID));
            if (ranked.Count > 0) return ranked;
            // None of these candidates matched a positive feature. Do not keep
            // offering the same window or misreport it as source exhaustion.
            round.Consume(window.Select(item => item.Anime.ID));
        }
        // A bounded fetch may find only duplicates or excluded works. The
        // source cursors remain live; this is not genuine exhaustion.
        return [];
    }

    public async Task<IReadOnlyList<RecommendationFeaturePreference>>
        GetFeaturePreferencesAsync(
            CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FeatureKind, FeatureKey, DisplayName, Adjustment, UpdatedAt
            FROM recommendation_feature_preferences
            ORDER BY FeatureKind, DisplayName COLLATE NOCASE
            """;
        var result = new List<RecommendationFeaturePreference>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RecommendationFeaturePreference(
                (RecommendationFeatureKind)reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                (RecommendationAdjustment)reader.GetInt32(3),
                DateTimeOffset.Parse(
                    reader.GetString(4),
                    CultureInfo.InvariantCulture)));
        }

        return result;
    }

    public async Task SetFeaturePreferenceAsync(
        RecommendationFeature feature,
        RecommendationAdjustment? adjustment,
        CancellationToken cancellationToken = default)
    {
        ValidateFeature(feature);
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        if (adjustment is null)
        {
            command.CommandText = """
                DELETE FROM recommendation_feature_preferences
                WHERE FeatureKind = @kind AND FeatureKey = @key
                """;
        }
        else
        {
            command.CommandText = """
                INSERT INTO recommendation_feature_preferences(
                    FeatureKind, FeatureKey, DisplayName,
                    Adjustment, UpdatedAt)
                VALUES(@kind, @key, @name, @adjustment, @updatedAt)
                ON CONFLICT(FeatureKind, FeatureKey) DO UPDATE SET
                    DisplayName = excluded.DisplayName,
                    Adjustment = excluded.Adjustment,
                    UpdatedAt = excluded.UpdatedAt
                """;
            command.Parameters.AddWithValue("@name", feature.DisplayName.Trim());
            command.Parameters.AddWithValue("@adjustment", (int)adjustment.Value);
            command.Parameters.AddWithValue(
                "@updatedAt",
                DateTimeOffset.UtcNow.ToString("O"));
        }

        command.Parameters.AddWithValue("@kind", (int)feature.Kind);
        command.Parameters.AddWithValue("@key", feature.Key.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InvalidateSnapshotAsync();
    }

    public async Task ClearFeaturePreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recommendation_feature_preferences";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InvalidateSnapshotAsync();
    }

    public async Task<IReadOnlyList<RecommendationHiddenAnime>>
        GetHiddenAnimeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AnimeId, TitleSnapshot, HiddenAt
            FROM recommendation_hidden_anime
            ORDER BY HiddenAt DESC
            """;
        var result = new List<RecommendationHiddenAnime>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RecommendationHiddenAnime(
                reader.GetInt32(0),
                reader.GetString(1),
                DateTimeOffset.Parse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture)));
        }

        return result;
    }

    public async Task HideAnimeAsync(
        int animeId,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(animeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recommendation_hidden_anime(
                AnimeId, TitleSnapshot, HiddenAt)
            VALUES(@animeId, @title, @hiddenAt)
            ON CONFLICT(AnimeId) DO UPDATE SET
                TitleSnapshot = excluded.TitleSnapshot,
                HiddenAt = excluded.HiddenAt
            """;
        command.Parameters.AddWithValue("@animeId", animeId);
        command.Parameters.AddWithValue("@title", title.Trim());
        command.Parameters.AddWithValue(
            "@hiddenAt",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InvalidateSnapshotAsync();
    }

    public async Task RestoreAnimeAsync(
        int animeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM recommendation_hidden_anime WHERE AnimeId = @animeId
            """;
        command.Parameters.AddWithValue("@animeId", animeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InvalidateSnapshotAsync();
    }

    public async Task ClearHiddenAnimeAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recommendation_hidden_anime";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InvalidateSnapshotAsync();
    }

    public async Task MarkNotInterestedAsync(
        int animeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _tracking.SetStatusAsync(
            animeId,
            AnimeTrackingStatus.NotInterested);
        await InvalidateSnapshotAsync();
    }

    private async Task InvalidateSnapshotAsync()
    {
        // Invalidate the in-flight result immediately; only cache I/O is serialized.
        Interlocked.Increment(ref _snapshotRevision);
        await _snapshotCacheGate.WaitAsync();
        try
        {
            await _cache.RemoveCacheAsync(SnapshotCacheKey);
        }
        finally
        {
            _snapshotCacheGate.Release();
        }
    }

    private async Task<bool> TryStoreSnapshotAsync(
        RecommendationSnapshot snapshot,
        long cacheGeneration,
        long snapshotRevision,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await _snapshotCacheGate.WaitAsync(cancellationToken);
        try
        {
            if (snapshotRevision != Interlocked.Read(ref _snapshotRevision))
                return false;
            await _cache.SetCacheAsync(SnapshotCacheKey,
                json, SnapshotRetention, cacheGeneration);
            return snapshotRevision == Interlocked.Read(ref _snapshotRevision);
        }
        finally
        {
            _snapshotCacheGate.Release();
        }
    }

    private async Task<IReadOnlyList<RecommendationSeed>> BuildSeedsAsync(
        IReadOnlyList<(
            int AnimeId,
            AnimeTrackingStatus Status,
            string UpdatedAt)> tracking,
        CancellationToken cancellationToken)
    {
        var seeds = new Dictionary<int, SeedAccumulator>();
        foreach (var item in tracking)
        {
            if (item.Status == AnimeTrackingStatus.Blocked)
            {
                continue;
            }

            AddSeed(seeds, item.AnimeId, null, StatusWeight(item.Status),
                item.Status switch
                {
                    AnimeTrackingStatus.Completed => RecommendationEvidenceSource.Completed,
                    AnimeTrackingStatus.Watching => RecommendationEvidenceSource.Watching,
                    AnimeTrackingStatus.Following => RecommendationEvidenceSource.Following,
                    AnimeTrackingStatus.PlanToWatch => RecommendationEvidenceSource.PlanToWatch,
                    AnimeTrackingStatus.Dropped => RecommendationEvidenceSource.Dropped,
                    AnimeTrackingStatus.NotInterested => RecommendationEvidenceSource.NotInterested,
                    _ => RecommendationEvidenceSource.Unknown,
                });
        }

        foreach (var item in await _archive.GetArchiveListAsync(
            cancellationToken))
        {
            if (item.Archive.PersonalRating is { } rating)
            {
                AddSeed(
                    seeds,
                    item.Archive.AnimeId,
                    item.Archive.TitleSnapshot,
                    rating - 5.5,
                    RecommendationEvidenceSource.PersonalRating);
            }
        }

        foreach (var item in await _history.GetHistoryAsync(
            100,
            cancellationToken))
        {
            var browseWeight = Math.Min(
                0.5,
                Math.Log2(item.ViewCount + 1) * 0.125);
            AddSeed(seeds, item.AnimeId, item.Title, browseWeight,
                RecommendationEvidenceSource.Browsing);
        }

        foreach (var plan in await _actionCenter.GetPlansAsync(
            includeArchived: false,
            cancellationToken))
        {
            AddSeed(
                seeds,
                plan.AnimeId,
                plan.TitleSnapshot,
                ((int)plan.Priority + 1) * 0.125,
                RecommendationEvidenceSource.CatchUpPlan);
        }

        var completedIds = await GetCompletedAnimeIdsAsync(cancellationToken);
        foreach (var animeId in completedIds)
        {
            AddSeed(seeds, animeId, null, 0.5,
                RecommendationEvidenceSource.EpisodeProgress);
        }

        var blocked = tracking
            .Where(item => item.Status == AnimeTrackingStatus.Blocked)
            .Select(item => item.AnimeId)
            .ToHashSet();
        return seeds.Values
            .Where(item => !blocked.Contains(item.AnimeId))
            .OrderByDescending(item => Math.Abs(item.NormalizedWeight))
            .Take(30)
            .Select(item => new RecommendationSeed(
                item.AnimeId,
                item.Title ?? string.Empty,
                item.NormalizedWeight,
                item.NormalizedSignals))
            .ToArray();
    }

    private async Task<HashSet<int>> GetCompletedAnimeIdsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT AnimeId FROM episode_progress WHERE IsCompleted = 1
            """;
        var result = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt32(0));
        }

        return result;
    }

    private static void AddSeed(
        Dictionary<int, SeedAccumulator> seeds,
        int animeId,
        string? title,
        double weight,
        RecommendationEvidenceSource source)
    {
        if (weight == 0)
        {
            return;
        }

        if (!seeds.TryGetValue(animeId, out var accumulator))
        {
            accumulator = new SeedAccumulator(animeId);
            seeds.Add(animeId, accumulator);
        }

        accumulator.Signals.Add(new RecommendationSignal(source, weight));
        if (!string.IsNullOrWhiteSpace(title))
        {
            accumulator.Title = title;
        }
    }

    private static double StatusWeight(AnimeTrackingStatus status) => status switch
    {
        AnimeTrackingStatus.Completed => 2,
        AnimeTrackingStatus.Watching => 1.5,
        AnimeTrackingStatus.Following => 1,
        AnimeTrackingStatus.PlanToWatch => 0.5,
        AnimeTrackingStatus.Dropped => -2,
        AnimeTrackingStatus.NotInterested => -3,
        _ => 0,
    };

    private static void ValidateFeature(RecommendationFeature feature)
    {
        if (!Enum.IsDefined(feature.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(feature));
        }

        if (string.IsNullOrWhiteSpace(feature.Key)
            || feature.Key.Length > 100
            || string.IsNullOrWhiteSpace(feature.DisplayName)
            || feature.DisplayName.Length > 100)
        {
            throw new ArgumentException("推荐特征无效。", nameof(feature));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshGate.Dispose();
        _snapshotCacheGate.Dispose();
    }

    private sealed class SeedAccumulator(int animeId)
    {
        public int AnimeId { get; } = animeId;

        public string? Title { get; set; }

        public List<RecommendationSignal> Signals { get; } = [];

        // Correlated actions on one title should not turn that title into an
        // unlimited number of independent votes for every one of its tags.
        public IReadOnlyList<RecommendationSignal> NormalizedSignals
        {
            get
            {
                var positives = Signals.Where(signal => signal.Weight > 0)
                    .Sum(signal => signal.Weight);
                var negatives = Signals.Where(signal => signal.Weight < 0)
                    .Sum(signal => signal.Weight);
                var positiveScale = positives > 4 ? 4 / positives : 1;
                var negativeScale = negatives < -3 ? -3 / negatives : 1;
                return Signals.Select(signal => signal with
                {
                    Weight = signal.Weight * (signal.Weight > 0
                        ? positiveScale : negativeScale),
                }).ToArray();
            }
        }

        public double NormalizedWeight => NormalizedSignals.Sum(signal => signal.Weight);
    }
}
