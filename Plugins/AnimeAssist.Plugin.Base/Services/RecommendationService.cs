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

    public IReadOnlyList<RecommendationFeatureProfile> LastProfile
    {
        get;
        private set;
    } = [];

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
                || snapshot.GeneratedAt >= DateTimeOffset.UtcNow - SnapshotFreshness
                    ? snapshot
                    : null;
        }
        catch (JsonException)
        {
            await _cache.RemoveCacheAsync(SnapshotCacheKey);
            return null;
        }
    }

    public async Task<RecommendationGeneration> RefreshAsync(
        CancellationToken cancellationToken = default,
        bool preferNewBatch = false,
        IReadOnlySet<int>? displayedIds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
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
            LastProfile = RecommendationScorer.BuildProfile(
                seeds,
                features,
                preferences,
                savedTags);

            IReadOnlyList<RecommendationItem> items;
            var personalized = LastProfile.Any(item => item.EffectiveScore > 0);
            if (personalized)
            {
                var candidates = await _candidates.GetCandidatesAsync(
                    LastProfile,
                    excluded,
                    cancellationToken);
                items = RecommendationScorer.Rank(
                    LastProfile,
                    candidates,
                    DateOnly.FromDateTime(DateTime.Today),
                    previousIds);
                if (items.Count == 0)
                {
                    items = await _candidates.GetPopularAsync(
                        excluded,
                        cancellationToken);
                    personalized = false;
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
                && previousSnapshot is not null)
            {
                return new RecommendationGeneration(
                    previousSnapshot,
                    LastProfile);
            }

            var snapshot = new RecommendationSnapshot(
                RecommendationSnapshot.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                personalized,
                items);
            await _cache.SetCacheAsync(
                SnapshotCacheKey,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                SnapshotRetention);
            return new RecommendationGeneration(snapshot, LastProfile);
        }
        finally
        {
            _refreshGate.Release();
        }
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
        await _cache.RemoveCacheAsync(SnapshotCacheKey);
    }

    public async Task ClearFeaturePreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recommendation_feature_preferences";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await _cache.RemoveCacheAsync(SnapshotCacheKey);
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
        await _cache.RemoveCacheAsync(SnapshotCacheKey);
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
        await _cache.RemoveCacheAsync(SnapshotCacheKey);
    }

    public async Task ClearHiddenAnimeAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recommendation_hidden_anime";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await _cache.RemoveCacheAsync(SnapshotCacheKey);
    }

    public async Task MarkNotInterestedAsync(
        int animeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _tracking.SetStatusAsync(
            animeId,
            AnimeTrackingStatus.NotInterested);
        await _cache.RemoveCacheAsync(SnapshotCacheKey);
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

            AddSeed(seeds, item.AnimeId, null, StatusWeight(item.Status));
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
                    rating - 5.5);
            }
        }

        foreach (var item in await _history.GetHistoryAsync(
            100,
            cancellationToken))
        {
            var browseWeight = Math.Min(
                0.5,
                Math.Log2(item.ViewCount + 1) * 0.125);
            AddSeed(seeds, item.AnimeId, item.Title, browseWeight);
        }

        foreach (var plan in await _actionCenter.GetPlansAsync(
            includeArchived: false,
            cancellationToken))
        {
            AddSeed(
                seeds,
                plan.AnimeId,
                plan.TitleSnapshot,
                ((int)plan.Priority + 1) * 0.125);
        }

        var completedIds = await GetCompletedAnimeIdsAsync(cancellationToken);
        foreach (var animeId in completedIds)
        {
            AddSeed(seeds, animeId, null, 0.5);
        }

        var blocked = tracking
            .Where(item => item.Status == AnimeTrackingStatus.Blocked)
            .Select(item => item.AnimeId)
            .ToHashSet();
        return seeds.Values
            .Where(item => !blocked.Contains(item.AnimeId))
            .OrderByDescending(item => Math.Abs(item.Weight))
            .Take(30)
            .Select(item => new RecommendationSeed(
                item.AnimeId,
                item.Title ?? string.Empty,
                item.Weight))
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
        double weight)
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

        accumulator.Weight += weight;
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
    }

    private sealed class SeedAccumulator(int animeId)
    {
        public int AnimeId { get; } = animeId;

        public string? Title { get; set; }

        public double Weight { get; set; }
    }
}
