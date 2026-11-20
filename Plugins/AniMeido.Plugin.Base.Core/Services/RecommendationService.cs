using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

public sealed class RecommendationService : IAsyncDisposable
{
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly TrackingService _tracking;
    private readonly SavedTagService _savedTags;
    private readonly ArchiveService _archive;
    private readonly BrowseHistoryService _history;
    private readonly ActionCenterService _actionCenter;
    private readonly RecommendationSnapshotStore _snapshots;
    private readonly RecommendationCandidateProvider _candidates;
    private readonly SemaphoreSlim _refreshGate = new(1);
    private readonly SemaphoreSlim _personalWriteGate = new(1);
    private RecommendationCandidateProvider.CandidateRound? _round;
    private DateTimeOffset _roundGeneratedAt;
    private RecommendationSnapshot? _committedSnapshot;
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
    private readonly ServiceOperationLifetime _lifetime = new();

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
        _snapshots = new RecommendationSnapshotStore(cache);
        _candidates = candidates;
    }

    public async Task<IReadOnlyList<RecommendationFeature>> GetPreviewTagsAsync(
        int animeId, CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        return await _candidates.GetTagsForPreviewAsync(animeId, request.Token);
    }

    public async Task<AnimeTrackingStatus?> GetTrackingStatusAsync(int animeId)
    {
        using var request = _lifetime.Begin();
        return await _tracking.GetStatusAsync(animeId);
    }

    public async Task<(AnimeTrackingStatus? Status, bool Changed)> ToggleFollowingAsync(int animeId,
        CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
        using var operation = _dbFactory.Access.BeginWrite();
        var result = await _tracking.ToggleFollowingAsync(animeId, cancellationToken);
        if (result.Changed) await _snapshots.InvalidateAsync();
        return result;
    }

    public IReadOnlyList<RecommendationFeatureProfile> LastProfile
    {
        get;
        private set;
    } = [];

    internal static bool IsSnapshotFresh(RecommendationSnapshot snapshot, DateTimeOffset now)
        => RecommendationSnapshotStore.IsFresh(snapshot, now);

    public async Task<RecommendationSnapshot?> GetCachedSnapshotAsync(
        bool allowExpired,
        CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        return await Task.Run(async () =>
        {
            var snapshot = await _snapshots.ReadAsync(allowExpired, request.Token);
            if (snapshot is null) return null;
            return await FilterRatedItemsCoreAsync(snapshot, request.Token);
        }, request.Token)
            .ConfigureAwait(false);
    }

    public async Task<RecommendationSnapshot> FilterRatedItemsAsync(RecommendationSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        return await Task.Run(() => FilterRatedItemsCoreAsync(snapshot, request.Token), request.Token)
            .ConfigureAwait(false);
    }

    private async Task<RecommendationSnapshot> FilterRatedItemsCoreAsync(RecommendationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            snapshot = RestoreCommittedSnapshot(snapshot);
            var rated = await ReadRatedAnimeIdsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var filtered = snapshot with { Items = snapshot.Items.Where(item => !rated.Contains(item.Anime.ID)).ToArray() };
            RememberFilteredSnapshot(filtered);
            if (_roundGeneratedAt == snapshot.GeneratedAt) _round?.Exclude(rated);
            return filtered;
        }
        finally { _refreshGate.Release(); }
    }

    private void RememberFilteredSnapshot(RecommendationSnapshot snapshot)
    {
        if (_committedSnapshot is null || (_committedSnapshot.GeneratedAt == snapshot.GeneratedAt
            && _committedSnapshot.SchemaVersion == snapshot.SchemaVersion))
            Volatile.Write(ref _committedSnapshot, snapshot);
    }

    private RecommendationSnapshot RestoreCommittedSnapshot(RecommendationSnapshot snapshot)
        => Volatile.Read(ref _committedSnapshot) is { } committed && committed.GeneratedAt == snapshot.GeneratedAt
            && committed.SchemaVersion == snapshot.SchemaVersion ? committed : snapshot;

    public async Task<RecommendationGeneration?> RefreshAsync(
        CancellationToken cancellationToken = default,
        bool preferNewBatch = false,
        IReadOnlySet<int>? displayedIds = null)
    {
        using var request = _lifetime.Begin(cancellationToken);
        return await Task.Run(() => RefreshCoreAsync(request.Token, preferNewBatch, displayedIds), request.Token)
            .ConfigureAwait(false);
    }

    private async Task<RecommendationGeneration?> RefreshCoreAsync(
        CancellationToken cancellationToken,
        bool preferNewBatch,
        IReadOnlySet<int>? displayedIds)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var generatedAt = Clock.GetUtcNow();
            var roundDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(generatedAt, Clock.LocalTimeZone).DateTime);
            var snapshotRevision = _snapshots.Revision;
            var cacheGeneration = _snapshots.CaptureCacheGeneration();
            var previousSnapshot = preferNewBatch
                ? await _snapshots.ReadAsync(
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
            var hidden = await ReadHiddenAnimeAsync(cancellationToken);
            var rated = await _archive.GetRatedArchiveSeedsAsync(cancellationToken);
            var excluded = tracking.Select(item => item.AnimeId)
                .Concat(hidden.Select(item => item.AnimeId))
                .Concat(rated.Select(item => item.AnimeId))
                .ToHashSet();
            if (previousIds is not null)
            {
                excluded.UnionWith(previousIds);
            }
            var preferences = await ReadFeaturePreferencesAsync(
                cancellationToken);
            preferences = preferences.Where(p => !p.Invalid).ToList();
            var savedTags = (await _savedTags.GetSavedTagStatesAsync()).Where(t => !t.Invalid).Select(t => t.Name).ToList();
            var seeds = await BuildSeedsAsync(
                tracking, rated,
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
            excluded = _candidates.ExpandKnownExcludedIds(excluded);

            IReadOnlyList<RecommendationItem> items;
            RecommendationCandidateProvider.CandidateRound? newRound = null;
            var personalized = profile.Any(item => item.IsActiveForRecommendation);
            if (personalized)
            {
                newRound = _candidates.CreateRound(profile, excluded, roundDate);
                items = await GenerateBatchAsync(newRound, profile,
                    cancellationToken);
                if (items.Count == 0 && !newRound.HasMore)
                {
                    items = await _candidates.GetPopularAsync(
                        newRound.ExcludedIds,
                        cancellationToken, profile);
                    personalized = false;
                    newRound = null;
                }
            }
            else
            {
                items = await _candidates.GetPopularAsync(
                    excluded,
                    cancellationToken, profile);
            }

            // 档案评分可在远端读取期间保存，发布结果前再次读取轻量 ID 集合。
            var currentRated = await ReadRatedAnimeIdsAsync(cancellationToken);
            excluded = _candidates.ExpandKnownExcludedIds(excluded);
            items = items.Where(item => !excluded.Contains(item.Anime.ID) && !currentRated.Contains(item.Anime.ID))
                .DistinctBy(item => item.Anime.ID)
                .Take(20)
                .ToArray();
            if (preferNewBatch
                && items.Count == 0
                && previousSnapshot is not null
                && newRound?.HasMore != true)
            {
                if (snapshotRevision != _snapshots.Revision)
                    return null;
                LastProfile = profile;
                var unavailable = _candidates.ExpandKnownExcludedIds(tracking.Select(item => item.AnimeId)
                    .Concat(hidden.Select(item => item.AnimeId)).Concat(currentRated));
                return new RecommendationGeneration(
                    previousSnapshot with { Items = previousSnapshot.Items.Where(item => !unavailable.Contains(item.Anime.ID)).ToArray() },
                    profile);
            }

            var snapshot = new RecommendationSnapshot(
                RecommendationSnapshot.CurrentSchemaVersion,
                generatedAt,
                personalized,
                items,
                personalized ? profile : null,
                newRound?.HasMoreAfterConsuming(items.Select(item => item.Anime.ID)) == true);
            if (!await _snapshots.TryWriteAsync(snapshot, cacheGeneration,
                snapshotRevision, cancellationToken))
                return null;
            LastProfile = profile;
            newRound?.Consume(items.Select(item => item.Anime.ID));
            _round = newRound;
            _roundGeneratedAt = snapshot.GeneratedAt;
            Volatile.Write(ref _committedSnapshot, snapshot);
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
        using var request = _lifetime.Begin(cancellationToken);
        return await Task.Run(() => LoadMoreCoreAsync(snapshot, removedIds, request.Token), request.Token)
            .ConfigureAwait(false);
    }

    private async Task<RecommendationPageBatch> LoadMoreCoreAsync(
        RecommendationSnapshot snapshot,
        IReadOnlySet<int> removedIds,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var suppliedIds = snapshot.Items.Select(item => item.Anime.ID).ToHashSet();
            snapshot = RestoreCommittedSnapshot(snapshot);
            var snapshotRevision = _snapshots.Revision;
            var cacheGeneration = _snapshots.CaptureCacheGeneration();
            var tracking = await _tracking.GetAllTrackingAsync();
            var hidden = await ReadHiddenAnimeAsync(cancellationToken);
            var rated = await ReadRatedAnimeIdsAsync(cancellationToken);
            var unavailable = _candidates.ExpandKnownExcludedIds(tracking.Select(item => item.AnimeId)
                .Concat(hidden.Select(item => item.AnimeId)).Concat(removedIds).Concat(rated));
            var shownIds = snapshot.Items.Select(item => item.Anime.ID).ToArray();
            snapshot = snapshot with { Items = snapshot.Items.Where(item => !unavailable.Contains(item.Anime.ID)).ToArray() };
            if (snapshot.SchemaVersion != RecommendationSnapshot.CurrentSchemaVersion)
                return new RecommendationPageBatch(snapshot with { HasMore = false }, []);
            if (!snapshot.IsPersonalized || !snapshot.HasMore)
            {
                RememberFilteredSnapshot(snapshot);
                return new RecommendationPageBatch(snapshot, snapshot.Items.Where(item => !suppliedIds.Contains(item.Anime.ID)).ToArray());
            }

            var profile = snapshot.RoundProfile ?? [];
            if (profile.Count == 0)
                return new RecommendationPageBatch(snapshot with { HasMore = false }, []);
            var excluded = unavailable.Concat(shownIds)
                .ToHashSet();
            if (_round is null || _roundGeneratedAt != snapshot.GeneratedAt)
            {
                _round = _candidates.CreateRound(profile, excluded,
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(snapshot.GeneratedAt, Clock.LocalTimeZone).DateTime));
                _roundGeneratedAt = snapshot.GeneratedAt;
            }
            _round.Exclude(excluded);
            var items = await GenerateBatchAsync(_round, profile, cancellationToken);
            var currentRated = await ReadRatedAnimeIdsAsync(cancellationToken);
            _round.Exclude(currentRated);
            items = items.Where(item => !currentRated.Contains(item.Anime.ID)).ToArray();
            snapshot = snapshot with { Items = snapshot.Items.Where(item => !currentRated.Contains(item.Anime.ID)).ToArray() };
            var updated = snapshot with
            {
                Items = snapshot.Items.Concat(items)
                    .DistinctBy(item => item.Anime.ID).ToArray(),
                HasMore = _round.HasMoreAfterConsuming(items.Select(item => item.Anime.ID)),
            };
            // An invalidated cache must stay removed, but this page can keep
            // browsing its existing round (including edits to next-round preferences).
            if (snapshotRevision == _snapshots.Revision
                && await _snapshots.MatchesRoundAsync(snapshot.GeneratedAt, cancellationToken))
            {
                if (await _snapshots.TryWriteAsync(updated, cacheGeneration, snapshotRevision, cancellationToken))
                    return CommitBatch(_round, updated, suppliedIds);
            }
            if (snapshotRevision != _snapshots.Revision)
            {
                var currentTracking = await _tracking.GetAllTrackingAsync();
                var currentHidden = await ReadHiddenAnimeAsync(cancellationToken);
                var currentExcluded = _candidates.ExpandKnownExcludedIds(currentTracking.Select(item => item.AnimeId)
                    .Concat(currentHidden.Select(item => item.AnimeId))
                    .Concat(removedIds)
                    .Concat(currentRated));
                _round.Exclude(currentExcluded);
                items = items.Where(item => !currentExcluded.Contains(item.Anime.ID))
                    .ToArray();
                updated = updated with
                {
                    Items = snapshot.Items.Concat(items)
                        .Where(item => !currentExcluded.Contains(item.Anime.ID))
                        .DistinctBy(item => item.Anime.ID).ToArray(),
                    HasMore = _round.HasMoreAfterConsuming(items.Select(item => item.Anime.ID)),
                };
            }
            cancellationToken.ThrowIfCancellationRequested();
            return CommitBatch(_round, updated, suppliedIds);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private RecommendationPageBatch CommitBatch(RecommendationCandidateProvider.CandidateRound round,
        RecommendationSnapshot snapshot, IReadOnlySet<int> suppliedIds)
    {
        // 缓存一旦完成写入，同步提交消费和恢复状态；离页由 ViewModel 决定是否展示。
        round.Consume(snapshot.Items.Select(item => item.Anime.ID));
        Volatile.Write(ref _committedSnapshot, snapshot);
        return new(snapshot, snapshot.Items.Where(item => !suppliedIds.Contains(item.Anime.ID)).ToArray());
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
                round.Today);
            if (ranked.Count > 0) return ranked;
            // None of these candidates matched a positive feature. Do not keep
            // offering the same window or misreport it as source exhaustion.
            round.Consume(window.Select(item => item.Anime.ID));
        }
        // A bounded fetch may find only duplicates or excluded works. The
        // source cursors remain live; this is not genuine exhaustion.
        return [];
    }

    public async Task<IReadOnlyList<RecommendationFeaturePreference>> GetFeaturePreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        return await Task.Run(() => ReadFeaturePreferencesAsync(request.Token), request.Token)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RecommendationFeaturePreference>> ReadFeaturePreferencesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FeatureKind, FeatureKey, DisplayName, Adjustment, UpdatedAt, Invalid
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
                    CultureInfo.InvariantCulture), reader.GetBoolean(5)));
        }

        return result;
    }

    public async Task SetFeaturePreferenceAsync(
        RecommendationFeature feature,
        RecommendationAdjustment? adjustment,
        CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
        using var operation = _dbFactory.Access.BeginWrite();
        ValidateFeature(feature);
        await WritePersonalDataAsync(async () =>
        {
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
            await _snapshots.InvalidateAsync();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearFeaturePreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
        using var operation = _dbFactory.Access.BeginWrite();
        await WritePersonalDataAsync(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM recommendation_feature_preferences";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await _snapshots.InvalidateAsync();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecommendationHiddenAnime>> GetHiddenAnimeAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        return await Task.Run(() => ReadHiddenAnimeAsync(request.Token), request.Token)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RecommendationHiddenAnime>> ReadHiddenAnimeAsync(
        CancellationToken cancellationToken)
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
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
        using var operation = _dbFactory.Access.BeginWrite();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(animeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await WritePersonalDataAsync(async () =>
        {
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
            await _snapshots.InvalidateAsync();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAnimeAsync(
        int animeId,
        CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
        using var operation = _dbFactory.Access.BeginWrite();
        await WritePersonalDataAsync(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM recommendation_hidden_anime WHERE AnimeId = @animeId
                """;
            command.Parameters.AddWithValue("@animeId", animeId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await _snapshots.InvalidateAsync();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearHiddenAnimeAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
        using var operation = _dbFactory.Access.BeginWrite();
        await WritePersonalDataAsync(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM recommendation_hidden_anime";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await _snapshots.InvalidateAsync();
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task WritePersonalDataAsync(Func<Task> write, CancellationToken cancellationToken)
    {
        await _personalWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(write, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _personalWriteGate.Release();
        }
    }

    public async Task MarkNotInterestedAsync(
        int animeId,
        CancellationToken cancellationToken = default)
    {
        using var request = _lifetime.Begin(cancellationToken);
        cancellationToken = request.Token;
        using var operation = _dbFactory.Access.BeginWrite();
        cancellationToken.ThrowIfCancellationRequested();
        await _tracking.SetStatusAsync(
            animeId,
            AnimeTrackingStatus.NotInterested);
        await _snapshots.InvalidateAsync();
    }

    private async Task<IReadOnlyList<RecommendationSeed>> BuildSeedsAsync(
        IReadOnlyList<(
            int AnimeId,
            AnimeTrackingStatus Status,
            string UpdatedAt)> tracking,
        IReadOnlyList<(int AnimeId, string TitleSnapshot, double PersonalRating)> rated,
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

        foreach (var item in rated)
        {
            AddSeed(
                seeds,
                item.AnimeId,
                item.TitleSnapshot,
                item.PersonalRating - 5.5,
                RecommendationEvidenceSource.PersonalRating);
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
            .Where(item => item.AnimeId > 0 && !blocked.Contains(item.AnimeId) && item.NormalizedWeight != 0)
            .OrderByDescending(item => Math.Abs(item.NormalizedWeight))
            .Take(30)
            .Select(item => new RecommendationSeed(
                item.AnimeId,
                item.Title ?? string.Empty,
                item.NormalizedWeight,
                item.NormalizedSignals))
            .ToArray();
    }

    private async Task<HashSet<int>> ReadRatedAnimeIdsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dbFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT AnimeId FROM anime_archives WHERE PersonalRating IS NOT NULL";
        var result = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetInt32(0));
        return _candidates.ExpandKnownExcludedIds(result);
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
        if (weight == 0 && source != RecommendationEvidenceSource.PersonalRating)
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

    public ValueTask DisposeAsync() => _lifetime.StopAsync(() =>
    {
        _refreshGate.Dispose();
        _personalWriteGate.Dispose();
        _snapshots.Dispose();
    });

    private sealed class SeedAccumulator(int animeId)
    {
        public int AnimeId { get; } = animeId;

        public string? Title { get; set; }

        public List<RecommendationSignal> Signals { get; } = [];

        // 明确意图优先；同一作品的浏览、计划与观看行为只贡献一次。
        public IReadOnlyList<RecommendationSignal> NormalizedSignals
        {
            get
            {
                var signal = Signals.FirstOrDefault(item => item.Source == RecommendationEvidenceSource.NotInterested)
                    ?? Signals.FirstOrDefault(item => item.Source == RecommendationEvidenceSource.PersonalRating)
                    ?? Signals.Where(item => item.Weight < 0).OrderBy(item => item.Weight).FirstOrDefault()
                    ?? Signals.OrderByDescending(item => item.Weight).ThenBy(item => item.Source).FirstOrDefault();
                return signal is null ? [] : [signal with { Weight = Math.Clamp(signal.Weight, -3, 4) }];
            }
        }

        public double NormalizedWeight => NormalizedSignals.Sum(signal => signal.Weight);
    }
}
