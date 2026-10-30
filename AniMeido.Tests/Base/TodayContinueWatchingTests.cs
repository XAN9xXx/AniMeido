using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed class TodayContinueWatchingTests : DbTestBase
{
    private readonly PersonalFeatureStateStore _featureState;

    public TodayContinueWatchingTests()
    {
        _featureState = new PersonalFeatureStateStore(DbFactory);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task PlayerAvailable_LargeUnstartedListKeepsEveryItemAndTrackingOrderWithoutRefetchingSeasonDetails(int count)
    {
        await RunProductionMigrationAsync();
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedTrackingAsync(Enumerable.Range(1, count)
            .Select(id => (id, AnimeTrackingStatus.Watching, at.AddSeconds(id))));
        var seasonal = new[] { Item(count), Item(3), Item(1) };
        var source = new WatchingSource { Schedule = seasonal.ToList() };
        using var fixture = CreateFixture(source);

        await LoadAsync(fixture);

        var vm = fixture.ViewModel;
        Assert.True(vm.IsPlaybackAvailable);
        Assert.True(vm.HasUnstarted);
        Assert.Equal(count, vm.ContinueWatching.Count);
        Assert.Equal(Enumerable.Range(1, count).Reverse(), vm.ContinueWatching.Select(entry => entry.Anime.ID));
        Assert.All(vm.ContinueWatching, entry =>
        {
            Assert.Equal("尚无播放记录", entry.DisplaySubtitle);
            Assert.False(entry.HasProgress);
        });
        foreach (var cached in seasonal)
            Assert.Same(cached, vm.ContinueWatching.Single(entry => entry.Anime.ID == cached.ID).Anime);
        var requested = source.DetailRequests.ToArray();
        Assert.Equal(count - 3, requested.Length);
        Assert.Equal(Enumerable.Range(1, count).Except(new[] { count, 3, 1 }), requested.Order());
        Assert.Empty(vm.RecentActivity);
        Assert.Empty(vm.Plans);
        Assert.Equal(count, (await fixture.Tracking.GetAllTrackingAsync()).Count);
        Assert.Equal(0, fixture.Player.LaunchCalls);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task PlayerAvailable_UnstartedEligibilityUsesStatusesAnyProgressAndActiveRatherThanArchivedPlans()
    {
        await RunProductionMigrationAsync();
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        (int Id, AnimeTrackingStatus Status)[] statuses =
        [
            (30, AnimeTrackingStatus.Watching),
            (31, AnimeTrackingStatus.PlanToWatch),
            (32, AnimeTrackingStatus.Watching),
            (33, AnimeTrackingStatus.PlanToWatch),
            (34, AnimeTrackingStatus.Watching),
            (35, AnimeTrackingStatus.Following),
            (36, AnimeTrackingStatus.Completed),
            (37, AnimeTrackingStatus.Dropped),
            (38, AnimeTrackingStatus.NotInterested),
            (39, AnimeTrackingStatus.Blocked),
            (40, AnimeTrackingStatus.Watching),
            (41, AnimeTrackingStatus.PlanToWatch),
            (42, AnimeTrackingStatus.Watching),
        ];
        await SeedTrackingAsync(statuses.Select(row => (row.Id, row.Status, at.AddSeconds(row.Id))));
        var cached = new[] { Item(40), Item(32) };
        var source = new WatchingSource { Schedule = cached.ToList() };
        using var fixture = CreateFixture(source);
        foreach (var id in new[] { 31, 33, 34, 40 })
            await fixture.ActionCenter.UpsertPlanAsync(id, $"计划{id}", AnimePlanPriority.Normal, null, id);
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE anime_plans SET ArchivedAt=@at WHERE AnimeId IN (31,40)";
            command.Parameters.AddWithValue("@at", at.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await fixture.ActionCenter.RecordAsync(new AnimePlaybackProgress(
            "zero-position", 32, 1, 0, 1200, false, at.AddMinutes(1)));
        await fixture.ActionCenter.RecordAsync(new AnimePlaybackProgress(
            "positive-position", 42, 1, 120, 1200, false, at.AddMinutes(2)));

        await LoadAsync(fixture);

        var vm = fixture.ViewModel;
        // 已归档的31仍是补番中；兼容补建会认出旧计划，不重新建成活动计划。
        // 没有任何计划的41则仍由现有兼容逻辑补建，因此不重复出现在这里。
        Assert.Equal(new[] { 40, 31, 30 }, vm.ContinueWatching.Select(entry => entry.Anime.ID));
        Assert.Equal(new[] { 33, 34, 41 }, vm.Plans.Select(entry => entry.Plan.AnimeId).Order());
        Assert.Equal(new[] { 42, 32 }, vm.RecentActivity.Select(entry => entry.Anime.ID));
        var zero = vm.RecentActivity.Single(entry => entry.Anime.ID == 32);
        Assert.True(zero.HasProgress);
        Assert.Equal(0, zero.ProgressPercent);
        Assert.Same(cached[0], vm.ContinueWatching[0].Anime);
        Assert.Same(cached[1], zero.Anime);
        var archived = (await fixture.ActionCenter.GetPlansAsync(includeArchived: true))
            .Where(plan => plan.ArchivedAt is not null);
        Assert.Equal(new[] { 31, 40 }, archived.Select(plan => plan.AnimeId).Order());
        Assert.DoesNotContain(source.DetailRequests, id => id is 35 or 36 or 37 or 38 or 39);
        Assert.Equal(0, fixture.Player.LaunchCalls);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task PlayerAvailable_NullAndKnownDetailFailuresKeepTheExistingSkipBehavior()
    {
        await RunProductionMigrationAsync();
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedTrackingAsync(Enumerable.Range(1, 8)
            .Select(id => (id, AnimeTrackingStatus.Watching, at.AddSeconds(id))));
        var cached = Item(8);
        var source = new WatchingSource
        {
            Schedule = [cached],
            Detail = (id, _) => id switch
            {
                1 => Task.FromResult<Anime?>(Item(1)),
                2 => Task.FromResult<Anime?>(null),
                3 => Task.FromException<Anime?>(new HttpRequestException("offline")),
                4 => Task.FromException<Anime?>(new MetadataServiceException("unavailable")),
                5 => Task.FromException<Anime?>(new InvalidOperationException("invalid detail")),
                6 => Task.FromException<Anime?>(new JsonException("invalid json")),
                7 => Task.FromException<Anime?>(new TaskCanceledException("request timeout")),
                _ => throw new InvalidOperationException("Season details must be reused"),
            },
        };
        using var fixture = CreateFixture(source);

        await LoadAsync(fixture);

        Assert.Equal(new[] { 8, 1 }, fixture.ViewModel.ContinueWatching.Select(entry => entry.Anime.ID));
        Assert.Equal(Enumerable.Range(1, 7), source.DetailRequests.Order());
        Assert.Same(cached, fixture.ViewModel.ContinueWatching[0].Anime);
        Assert.True(fixture.ViewModel.HasUnstarted);
        Assert.Null(fixture.ViewModel.ErrorMessage);
        Assert.False(fixture.ViewModel.IsLoadInterrupted);
    }

    [Fact]
    public async Task PlayerAvailable_CompositeSourceKeepsLocalAndDeactivatedPlaceholdersWithoutRemoteDetailRequests()
    {
        await RunProductionMigrationAsync();
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedTrackingAsync([
            (17, AnimeTrackingStatus.Watching, at),
            (-9, AnimeTrackingStatus.Watching, at.AddSeconds(1)),
        ]);
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO legacy_anime_refs(LocalId,LegacyBangumiId,Reason,TitleSnapshot,RecordedAt)
                VALUES(-9,9,'not_found','本地保留标题',@at);
                INSERT INTO public_id_issues(AnimeId,Kind,TitleSnapshot,DetectedAt)
                VALUES(17,'deactivated','已停用标题',@at)
                """;
            command.Parameters.AddWithValue("@at", at.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        using var handler = new EmptyMetadataHandler();
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(MetadataServiceSettings.QueryBaseUrl),
        };
        var remote = new MetadataDataSource(
            NullLogger<MetadataDataSource>.Instance, client, new CacheService(DbFactory));
        using var fixture = CreateFixture(new PersonalAnimeDataSource(remote, DbFactory));

        await LoadAsync(fixture);

        var entries = fixture.ViewModel.ContinueWatching;
        Assert.Equal(new[] { -9, 17 }, entries.Select(entry => entry.Anime.ID));
        Assert.Equal(new[] { "本地保留标题", "已停用标题" }, entries.Select(entry => entry.Anime.Title));
        Assert.All(entries, entry =>
        {
            Assert.IsType<UnavailableAnime>(entry.Anime);
            Assert.Null(entry.Anime.CoverURL);
            Assert.Equal("尚无播放记录 · 资料不可用", entry.DisplaySubtitle);
        });
        Assert.DoesNotContain(handler.Paths, path => path is "/v1/works/-9" or "/v1/works/17");
        Assert.Null(fixture.ViewModel.ErrorMessage);
        Assert.Equal(0, fixture.Player.LaunchCalls);
    }

    private async Task SeedTrackingAsync(IEnumerable<(int Id, AnimeTrackingStatus Status, DateTimeOffset At)> rows)
    {
        using var connection = await DbFactory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO tracking(AnimeId,Status,UpdatedAt) VALUES(@id,@status,@at)";
        var id = command.Parameters.AddWithValue("@id", 0);
        var status = command.Parameters.AddWithValue("@status", 0);
        var at = command.Parameters.AddWithValue("@at", "");
        foreach (var row in rows)
        {
            id.Value = row.Id;
            status.Value = (int)row.Status;
            at.Value = row.At.ToString("O");
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    private WatchingFixture CreateFixture(IAnimeDataSource source)
    {
        var tracking = new TrackingService(DbFactory);
        var actionCenter = new ActionCenterService(DbFactory);
        var reminders = new PlanReminderCoordinator(actionCenter, new NoopNotifications(), new NoopNavigator());
        var player = new AvailablePlayer();
        var vm = new TodayViewModel(
            source, tracking, _featureState, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory), NullLogger.Instance)
        {
            IsPlaybackAvailable = player.IsAvailable,
        };
        return new WatchingFixture(vm, tracking, actionCenter, player, reminders);
    }

    private static async Task LoadAsync(WatchingFixture fixture)
    {
        try
        {
            await fixture.ViewModel.LoadAsync();
            await fixture.ViewModel.Theme.PendingLoad;
        }
        finally
        {
            fixture.ViewModel.Theme.CancelLoad();
            await fixture.ViewModel.Theme.PendingLoad;
        }
    }

    private static Anime Item(int id) => new(
        id, $"详情{id}", null, [], DateOnly.FromDateTime(DateTime.Today),
        $"https://example.invalid/{id}.jpg", string.Empty, DateTime.Today.Year, DateTime.Today.Month,
        AnimeListPresentation.ToBangumiWeekday(DateTime.Today.DayOfWeek));

    private sealed class WatchingSource : IAnimeDataSource
    {
        public List<Anime> Schedule { get; init; } = [];
        public ConcurrentQueue<int> DetailRequests { get; } = new();
        public Func<int, CancellationToken, Task<Anime?>>? Detail { get; init; }

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DetailRequests.Enqueue(animeID);
            return Detail?.Invoke(animeID, ct) ?? Task.FromResult<Anime?>(Item(animeID));
        }

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => Task.FromResult(Schedule);
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => Task.FromResult(new List<Anime>());
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<Studio>());
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<Tag>());
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<VoiceActor>());
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<CharacterRole>());
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Task.FromResult(new List<PersonWork>());
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
            CancellationToken ct, string? airDateFrom = null, string? airDateTo = null) => Task.FromResult((new List<Anime>(), 0));
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(
            string keyword, int offset, CancellationToken ct) => Task.FromResult((new List<Anime>(), 0, 0));
    }

    private sealed class EmptyMetadataHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Enqueue(request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"dataVersion\":\"20261026.1\",\"total\":0,\"items\":[]}", Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("X-Data-Version", "20261026.1");
            return Task.FromResult(response);
        }
    }

    private sealed class AvailablePlayer : IAnimePlaybackLauncher
    {
        public bool IsAvailable => true;
        public int LaunchCalls { get; private set; }
        public event EventHandler? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public Task LaunchAsync(AnimePlaybackContext context, CancellationToken cancellationToken = default)
        {
            LaunchCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class NoopNotifications : IAppNotificationService
    {
        public bool IsSupported => true;
        public bool NotificationsEnabled => true;
        public Task ScheduleAsync(AppNotificationRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelAsync(string group, string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelGroupAsync(string group, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OpenNotificationSettingsAsync() => Task.CompletedTask;
        public IDisposable RegisterActivationHandler(
            string category, Func<AppNotificationActivation, CancellationToken, Task> handler) => new Registration();

        private sealed class Registration : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class NoopNavigator : IPlanReminderNavigation
    {
        public void OpenPlan(int animeId)
        {
        }
    }

    private sealed class WatchingFixture(
        TodayViewModel viewModel,
        TrackingService tracking,
        ActionCenterService actionCenter,
        AvailablePlayer player,
        PlanReminderCoordinator reminders) : IDisposable
    {
        public TodayViewModel ViewModel { get; } = viewModel;
        public TrackingService Tracking { get; } = tracking;
        public ActionCenterService ActionCenter { get; } = actionCenter;
        public AvailablePlayer Player { get; } = player;

        public void Dispose()
        {
            ViewModel.SuspendPlanDetails();
            ViewModel.Theme.CancelLoad();
            reminders.Dispose();
        }
    }
}
