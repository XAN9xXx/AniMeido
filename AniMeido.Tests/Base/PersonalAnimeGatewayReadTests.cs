using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PersonalAnimeGatewayReadTests : DbTestBase
{
    private static readonly DateTimeOffset At = new(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public async Task Query_SearchStatusAndLimitFilterConcreteFieldsAndDeduplicateSources()
    {
        await RunProductionMigrationAsync();
        using var fixture = new ReadFixture(DbFactory);
        await SeedAsync(fixture);
        await GuardAgainstWritesAsync();
        var before = await SnapshotAsync();

        var selected = await fixture.Gateway.QuerySelectionAsync(new PersonalAnimeSelectionQuery(
            SearchText: " alpha ", TrackingStatuses: [AnimeTrackingStatus.Watching], Limit: 1));
        var item = Assert.Single(selected);
        Assert.Equal(17, item.AnimeId);
        Assert.Equal("Alpha Series", item.Title);
        Assert.Equal(AnimeTrackingStatus.Watching, item.TrackingStatus);
        Assert.True(item.HasPlan);
        Assert.True(item.HasArchive);
        Assert.Equal(8.5, item.PersonalRating);
        Assert.Equal(At, item.UpdatedAt);
        var all = await fixture.Gateway.QuerySelectionAsync(new PersonalAnimeSelectionQuery());
        Assert.Equal(new[] { 17, 18, 19, 20 }, all.Select(value => value.AnimeId).Order());
        Assert.Equal(4, all.Select(value => value.AnimeId).Distinct().Count());
        Assert.Equal("Gamma History", all.Single(value => value.AnimeId == 20).Title);
        Assert.Null(all.Single(value => value.AnimeId == 20).TrackingStatus);
        Assert.Equal(new[] { 17 }, (await fixture.Gateway.QuerySelectionAsync(new(PlansOnly: true))).Select(value => value.AnimeId));
        Assert.Equal(new[] { 17, 18, 19 }, (await fixture.Gateway.QuerySelectionAsync(new(ArchivesOnly: true))).Select(value => value.AnimeId).Order());
        Assert.Equal(0, fixture.Source.Calls);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 200)]
    public async Task Query_ClampsLimitToContractBounds(int requested, int expected)
    {
        await RunProductionMigrationAsync();
        using var fixture = new ReadFixture(DbFactory);
        await fixture.Cache.CleanExpiredAsync();
        for (var id = 1; id <= 210; id++)
            await fixture.Tracking.SetStatusWithTimestampAsync(id, AnimeTrackingStatus.Watching, At.ToString("O"));
        await GuardAgainstWritesAsync();
        var before = await SnapshotAsync();
        var result = await fixture.Gateway.QuerySelectionAsync(new(Limit: requested));
        Assert.Equal(expected, result.Count);
        Assert.All(result, item =>
        {
            Assert.Equal($"Bangumi #{item.AnimeId}", item.Title);
            Assert.Equal(AnimeTrackingStatus.Watching, item.TrackingStatus);
            Assert.False(item.HasPlan);
            Assert.False(item.HasArchive);
        });
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(PersonalAnimeDataCategory.None)]
    [InlineData(PersonalAnimeDataCategory.PublicMetadata)]
    [InlineData(PersonalAnimeDataCategory.Tracking)]
    [InlineData(PersonalAnimeDataCategory.PlansAndProgress)]
    [InlineData(PersonalAnimeDataCategory.PersonalRating)]
    [InlineData(PersonalAnimeDataCategory.ArchiveTextAndHistory)]
    [InlineData(PersonalAnimeDataCategory.BrowseSummary)]
    public async Task Context_OnlyRequestedCategoryIsIncludedAndDuplicateIdsAreRemoved(PersonalAnimeDataCategory category)
    {
        await RunProductionMigrationAsync();
        using var fixture = new ReadFixture(DbFactory);
        await SeedAsync(fixture);
        await GuardAgainstWritesAsync();
        var before = await SnapshotAsync();
        var snapshot = await fixture.Gateway.BuildContextAsync(new("  test-purpose  ", [17, 17, 0, -1], category));
        Assert.Equal("test-purpose", snapshot.Purpose);
        Assert.Equal(category, snapshot.Categories);
        var item = Assert.Single(snapshot.Items);
        Assert.Equal(17, item.AnimeId);
        var publicData = category == PersonalAnimeDataCategory.PublicMetadata;
        Assert.Equal(publicData ? "Public Alpha" : category is PersonalAnimeDataCategory.PersonalRating or PersonalAnimeDataCategory.ArchiveTextAndHistory ? "Alpha Series" : "Plan Alpha", item.Title);
        Assert.Equal(publicData ? "公开简介" : null, item.Description);
        Assert.Equal(publicData ? new DateOnly(2025, 1, 1) : (DateOnly?)null, item.AirDate);
        Assert.Equal(publicData ? 7.5 : (double?)null, item.BangumiScore);
        Assert.Equal(publicData ? new[] { "公开标签" } : [], item.BangumiTags);
        Assert.Equal(publicData ? new[] { "公开公司" } : [], item.Studios);
        Assert.Equal(publicData ? new[] { "公开声优" } : [], item.VoiceActors);
        Assert.Equal(category == PersonalAnimeDataCategory.Tracking ? AnimeTrackingStatus.Watching : (AnimeTrackingStatus?)null, item.TrackingStatus);
        if (category == PersonalAnimeDataCategory.PlansAndProgress)
        {
            Assert.NotNull(item.Plan);
            Assert.Equal((int)AnimePlanPriority.High, item.Plan.Priority);
            Assert.Equal(new DateOnly(2025, 6, 1), item.Plan.TargetStartDate);
            Assert.Equal(7, item.Plan.SortOrder);
            Assert.NotNull(item.Progress);
            Assert.Equal(3, item.Progress.CurrentEpisode);
            Assert.Equal(420, item.Progress.PositionSeconds);
            Assert.Equal(1200, item.Progress.DurationSeconds);
        }
        else { Assert.Null(item.Plan); Assert.Null(item.Progress); }
        Assert.Equal(category == PersonalAnimeDataCategory.PersonalRating ? 8.5 : (double?)null, item.PersonalRating);
        Assert.Equal(category == PersonalAnimeDataCategory.ArchiveTextAndHistory ? "私人笔记" : null, item.ArchiveSummary);
        if (category == PersonalAnimeDataCategory.ArchiveTextAndHistory)
        {
            var entry = Assert.Single(item.ArchiveEntries);
            Assert.Equal("第3集私人感想", entry.Body);
            Assert.Equal(3, entry.EpisodeNumber);
            var watch = Assert.Single(item.WatchHistory.Where(value => value.IsManual));
            Assert.Equal("manual-fixture", watch.EventId);
            Assert.Equal("补录备注", watch.Note);
            Assert.Equal(1, watch.EpisodeFrom);
            Assert.Equal(2, watch.EpisodeTo);
        }
        else { Assert.Empty(item.ArchiveEntries); Assert.Empty(item.WatchHistory); }
        if (category == PersonalAnimeDataCategory.BrowseSummary)
        {
            Assert.NotNull(item.BrowseSummary);
            Assert.Equal(2, item.BrowseSummary.ViewCount);
        }
        else Assert.Null(item.BrowseSummary);
        Assert.Empty(snapshot.SavedBangumiTags);
        Assert.Empty(snapshot.PreferenceProfile);
        Assert.Equal(publicData ? 4 : 0, fixture.Source.Calls);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(PersonalAnimeDataCategory.SavedBangumiTags)]
    [InlineData(PersonalAnimeDataCategory.RecommendationProfile)]
    public async Task Context_ProfileOnlyRequestsExcludeUnrequestedGlobalCategory(PersonalAnimeDataCategory category)
    {
        await RunProductionMigrationAsync();
        using var fixture = new ReadFixture(DbFactory);
        await SeedAsync(fixture);
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO recommendation_feature_preferences VALUES('Tag', 'fixture', '偏好标签', 1, '2025-01-01T00:00:00Z')";
            await command.ExecuteNonQueryAsync();
        }
        await GuardAgainstWritesAsync();
        var before = await SnapshotAsync();
        var result = await fixture.Gateway.BuildContextAsync(new("profile", [], category));
        Assert.Empty(result.Items);
        if (category == PersonalAnimeDataCategory.SavedBangumiTags)
        {
            Assert.Equal(new[] { "收藏标签" }, result.SavedBangumiTags);
            Assert.Empty(result.PreferenceProfile);
        }
        else
        {
            Assert.Empty(result.SavedBangumiTags);
            var feature = Assert.Single(result.PreferenceProfile);
            Assert.Equal("fixture", feature.Key);
            Assert.Equal("偏好标签", feature.DisplayName);
            Assert.Equal(1, feature.ManualAdjustment);
            Assert.Equal(0, feature.InferredScore);
            Assert.Empty(feature.EvidenceTitles);
        }
        Assert.Equal(0, fixture.Source.Calls);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task Context_MissingPublicAndPersonalDataReturnsPlaceholderRatherThanDroppingId()
    {
        await RunProductionMigrationAsync();
        using var fixture = new ReadFixture(DbFactory);
        await fixture.Cache.CleanExpiredAsync();
        await GuardAgainstWritesAsync();
        var before = await SnapshotAsync();
        var result = await fixture.Gateway.BuildContextAsync(new("missing", [999],
            PersonalAnimeDataCategory.PublicMetadata | PersonalAnimeDataCategory.Tracking |
            PersonalAnimeDataCategory.PlansAndProgress | PersonalAnimeDataCategory.PersonalRating |
            PersonalAnimeDataCategory.ArchiveTextAndHistory | PersonalAnimeDataCategory.BrowseSummary));
        var item = Assert.Single(result.Items);
        Assert.Equal(999, item.AnimeId);
        Assert.Equal("Bangumi #999", item.Title);
        Assert.Null(item.Description); Assert.Null(item.AirDate); Assert.Null(item.BangumiScore);
        Assert.Null(item.TrackingStatus); Assert.Null(item.Plan); Assert.Null(item.Progress);
        Assert.Null(item.PersonalRating); Assert.Null(item.ArchiveSummary); Assert.Null(item.BrowseSummary);
        Assert.Empty(item.BangumiTags); Assert.Empty(item.Studios); Assert.Empty(item.VoiceActors);
        Assert.Empty(item.ArchiveEntries); Assert.Empty(item.WatchHistory);
        Assert.Equal(4, fixture.Source.Calls);
        Assert.Equal(before, await SnapshotAsync());
    }

    private static async Task SeedAsync(ReadFixture f)
    {
        await f.Cache.CleanExpiredAsync();
        await f.Archive.UpsertArchiveAsync(17, "Alpha Series", 8.5, "私人笔记");
        await f.Archive.UpsertArchiveAsync(18, "ALPHA Film", 6, "其他笔记");
        await f.Archive.UpsertArchiveAsync(19, "Beta", null, "无评分");
        await f.Archive.AddEntryAsync(17, At, 3, "第3集私人感想");
        await f.Archive.AddManualWatchEventAsync(new ManualWatchEvent("manual-fixture", 17, "Alpha Series", At, 1, 2, 48, "补录备注", At));
        await f.Action.RecordAsync(new AnimePlaybackProgress("playback-fixture", 17, 3, 420, 1200, false, At));
        await f.Tracking.SetStatusWithTimestampAsync(17, AnimeTrackingStatus.Watching, At.ToString("O"));
        await f.Tracking.SetStatusWithTimestampAsync(18, AnimeTrackingStatus.Completed, At.AddDays(-1).ToString("O"));
        await f.Tracking.SetStatusWithTimestampAsync(19, AnimeTrackingStatus.Watching, At.AddDays(-2).ToString("O"));
        await f.Action.UpsertPlanAsync(17, "Plan Alpha", AnimePlanPriority.High, new DateOnly(2025, 6, 1), 7);
        await f.History.RecordAsync(17, "History Alpha");
        await f.History.RecordAsync(17, "History Alpha");
        await f.History.RecordAsync(20, "Gamma History");
        await f.Saved.SaveTagAsync("收藏标签");
    }

    private async Task GuardAgainstWritesAsync()
    {
        using var connection = await DbFactory.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var names = new List<string>();
        using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        foreach (var name in names)
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE TRIGGER \"read_guard_{name}_{operation}\" BEFORE {operation} ON \"{name}\" BEGIN SELECT RAISE(ABORT, 'Read request attempted a write'); END";
                await command.ExecuteNonQueryAsync();
            }
    }

    private async Task<string> SnapshotAsync()
    {
        using var connection = await DbFactory.OpenAsync();
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var names = new List<string>();
        using (var reader = await tables.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        var result = new List<string>();
        foreach (var name in names)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{name}\"";
            using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values.Select(value => value is DBNull ? null : value)));
            }
            result.Add(name + ":" + string.Join("|", rows.Order(StringComparer.Ordinal)));
        }
        return string.Join("\n", result);
    }

    private sealed class ReadFixture : IDisposable
    {
        public MetadataSource Source { get; } = new();
        public TrackingService Tracking { get; }
        public ArchiveService Archive { get; }
        public ActionCenterService Action { get; }
        public BrowseHistoryService History { get; }
        public SavedTagService Saved { get; }
        public CacheService Cache { get; }
        public PersonalAnimeDataGateway Gateway { get; }
        private readonly RecommendationCandidateProvider _candidates;
        private readonly RecommendationService _recommendations;
        private readonly PlanReminderCoordinator _reminders;
        public ReadFixture(SqliteConnectionFactory factory)
        {
            Tracking = new(factory); Archive = new(factory); Action = new(factory);
            History = new(factory); Saved = new(factory); Cache = new(factory);
            _candidates = new RecommendationCandidateProvider(Source, NullLogger<RecommendationCandidateProvider>.Instance);
            _recommendations = new RecommendationService(factory, Tracking, Saved, Archive, History, Action, Cache, _candidates);
            var notifications = new NoWriteNotifications();
            _reminders = new PlanReminderCoordinator(Action, notifications, new NoNavigation());
            Gateway = new PersonalAnimeDataGateway(factory, Source, Tracking, Action, Archive, Saved, History, _recommendations, _reminders, notifications);
        }
        public void Dispose() { _reminders.Dispose(); _recommendations.Dispose(); _candidates.Dispose(); }
    }

    private sealed class MetadataSource : IAnimeDataSource
    {
        public int Calls { get; private set; }
        public Task<Anime?> GetAnimeDetailAsync(int id, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<Anime?>(id == 17 ? new Anime(17, "Public Alpha", null, [], new DateOnly(2025, 1, 1), null, "公开简介", 2025, 1, Score: 7.5) : null);
        }
        public Task<List<Tag>> GetTagsAsync(int id, CancellationToken ct) { Calls++; return Task.FromResult<List<Tag>>(id == 17 ? [new Tag("公开标签")] : []); }
        public Task<List<Studio>> GetStudioAsync(int id, CancellationToken ct) { Calls++; return Task.FromResult<List<Studio>>(id == 17 ? [new Studio(1, "公开公司", null)] : []); }
        public Task<List<VoiceActor>> GetCVsAsync(int id, CancellationToken ct) { Calls++; return Task.FromResult<List<VoiceActor>>(id == 17 ? [new VoiceActor(1, "公开声优", null)] : []); }
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoWriteNotifications : IAppNotificationService, IDisposable
    {
        public bool IsSupported => false;
        public bool NotificationsEnabled => false;
        public IDisposable RegisterActivationHandler(string category, Func<AppNotificationActivation, CancellationToken, Task> handler) => this;
        public Task ScheduleAsync(AppNotificationRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Read invoked notification write");
        public Task CancelAsync(string group, string tag, CancellationToken ct = default) => throw new InvalidOperationException("Read invoked notification write");
        public Task CancelGroupAsync(string group, CancellationToken ct = default) => throw new InvalidOperationException("Read invoked notification write");
        public Task OpenNotificationSettingsAsync() => throw new InvalidOperationException("Read invoked settings");
        public void Dispose() { }
    }
    private sealed class NoNavigation : IPluginNavigator
    {
        public void Navigate(Type pageType, object? parameter = null) => throw new InvalidOperationException("Read invoked navigation");
    }
}
