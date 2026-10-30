using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class TrackingServiceSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);

    private readonly PersonalDatabaseFixture _database = new();
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly PersonalFeatureStateStore _stateStore;

    public TrackingServiceSchedulingTests()
    {
        _dbFactory = _database.Factory;
        _stateStore = new PersonalFeatureStateStore(_dbFactory);
    }

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task DatabaseStop_WaitsForCommittedStatusNotificationBeforeFinishing()
    {
        var notifications = new GatedNotifications();
        var tracking = new TrackingService(_dbFactory, notifications);
        var operation = tracking.SetStatusAsync(27, AnimeTrackingStatus.Completed);
        try
        {
            await notifications.Called.WaitAsync(CompletionBudget);
            Assert.Equal(AnimeTrackingStatus.Completed, await tracking.GetStatusAsync(27));

            var stopping = _dbFactory.StopAsync();
            Assert.False(stopping.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                tracking.SetStatusAsync(28, AnimeTrackingStatus.Watching));

            notifications.Release();
            await operation.WaitAsync(CompletionBudget);
            await stopping.WaitAsync(CompletionBudget);
        }
        finally
        {
            notifications.Release();
            await operation.WaitAsync(CompletionBudget);
        }
    }

    [Fact]
    public async Task GetBlocked_ReturnsItsTaskWhileSqliteIsLocked_AndKeepsEveryResult()
    {
        var tracking = new TrackingService(_dbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Blocked);
        await tracking.SetStatusAsync(2, AnimeTrackingStatus.Blocked);
        await tracking.SetStatusAsync(3, AnimeTrackingStatus.Watching);
        using var blocker = await LockDatabaseAsync();
        var ui = new PausedUiContext();
        var invocation = CallFromPausedContextThread(tracking.GetBlockedAnimeIdsAsync, ui);
        Task operation;
        try
        {
            operation = await invocation.Returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            blocker.Dispose();
            ui.Resume();
            await FinishInvocationAsync(invocation);
        }

        var blocked = await (Task<HashSet<int>>)operation;
        Assert.Equal(new[] { 1, 2 }, blocked.Order());
    }

    [Fact]
    public async Task SetStatus_ReturnsWhileSqliteIsLocked_AndRetainsWriteAdmissionThroughNotification()
    {
        var setup = new TrackingService(_dbFactory);
        await setup.SetStatusAsync(2, AnimeTrackingStatus.PlanToWatch);
        var plans = new ActionCenterService(_dbFactory);
        await plans.UpsertPlanAsync(2, "Synthetic plan", AnimePlanPriority.Normal, null, 0);
        var notifications = new GatedNotifications();
        var tracking = new TrackingService(_dbFactory, notifications);
        using var blocker = await LockDatabaseAsync();
        var ui = new ObservedPausedUiContext();
        var invocation = CallFromPausedContextThread(
            () => tracking.SetStatusAsync(2, AnimeTrackingStatus.Completed), ui);
        Task operation;
        try
        {
            operation = await invocation.Returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);
            Assert.Empty(notifications.CancelledGroups);

            blocker.Dispose();
            await ui.Posted.WaitAsync(CompletionBudget);

            Assert.Empty(notifications.CancelledGroups);
            Assert.Equal(AnimeTrackingStatus.Completed, await setup.GetStatusAsync(2));
            Assert.NotNull((await plans.GetPlanAsync(2))?.ArchivedAt);
            Assert.Contains(await setup.GetTrackingEventsAsync(),
                entry => entry.AnimeId == 2 && entry.NewStatus == AnimeTrackingStatus.Completed);

            using var exclusive = _dbFactory.Access.BeginExclusive();
            var drained = exclusive.WaitForDrainAsync(CancellationToken.None);
            Assert.False(drained.IsCompleted);

            ui.Resume();
            await notifications.Called.WaitAsync(CompletionBudget);

            Assert.Equal(new[] { "anime-plan-2" }, notifications.CancelledGroups);
            Assert.False(operation.IsCompleted);
            Assert.False(drained.IsCompleted);

            notifications.Release();
            await operation.WaitAsync(CompletionBudget);
            await drained.WaitAsync(CompletionBudget);
        }
        finally
        {
            blocker.Dispose();
            notifications.Release();
            ui.Resume();
            await FinishInvocationAsync(invocation);
        }
    }

    [Fact]
    public async Task GetAllTracking_ReturnsWhileSqliteIsLocked_AndKeepsEveryFieldInTimestampOrder()
    {
        var tracking = new TrackingService(_dbFactory);
        await SeedOrderedTrackingAsync(tracking);

        var records = await RunWhileDatabaseLockedAsync(tracking.GetAllTrackingAsync);

        Assert.Equal(new[]
        {
            (10, AnimeTrackingStatus.Completed, "2026-01-05T00:00:00Z"),
            (30, AnimeTrackingStatus.Watching, "2026-01-04T00:00:00Z"),
            (20, AnimeTrackingStatus.Blocked, "2026-01-03T00:00:00Z"),
            (40, AnimeTrackingStatus.Watching, "2026-01-02T00:00:00Z"),
        }, records);
    }

    [Fact]
    public async Task GetGroupedTracking_ReturnsWhileSqliteIsLocked_AndKeepsEveryIdInTimestampOrder()
    {
        var tracking = new TrackingService(_dbFactory);
        await SeedOrderedTrackingAsync(tracking);

        var groups = await RunWhileDatabaseLockedAsync(tracking.GetAnimeIdsGroupedByStatusAsync);

        Assert.Equal(3, groups.Count);
        Assert.Equal(new[] { 30, 40 }, groups[AnimeTrackingStatus.Watching]);
        Assert.Equal(new[] { 10 }, groups[AnimeTrackingStatus.Completed]);
        Assert.Equal(new[] { 20 }, groups[AnimeTrackingStatus.Blocked]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetStatus_ReturnsWhileSqliteIsLocked_AndPreservesMissingOrExistingStatus(bool exists)
    {
        var tracking = new TrackingService(_dbFactory);
        if (exists)
        {
            await tracking.SetStatusWithTimestampAsync(
                2, AnimeTrackingStatus.Watching, "2026-01-02T00:00:00Z");
        }

        var status = await RunWhileDatabaseLockedAsync(() => tracking.GetStatusAsync(2));

        Assert.Equal(exists ? AnimeTrackingStatus.Watching : (AnimeTrackingStatus?)null, status);
    }

    [Theory]
    [InlineData(null, AnimeTrackingStatus.Following, true, 1)]
    [InlineData(AnimeTrackingStatus.Following, null, true, 0)]
    [InlineData(AnimeTrackingStatus.Watching, AnimeTrackingStatus.Watching, false, 0)]
    public async Task ToggleFollowing_ReturnsWhileSqliteIsLocked_AndPreservesEachBranch(
        AnimeTrackingStatus? initialStatus,
        AnimeTrackingStatus? expectedStatus,
        bool expectedChanged,
        int expectedEventCount)
    {
        var setup = new TrackingService(_dbFactory);
        if (initialStatus is { } status)
        {
            await setup.SetStatusWithTimestampAsync(2, status, "2026-01-02T00:00:00Z");
        }
        var notifications = new GatedNotifications();
        notifications.Release();
        var tracking = new TrackingService(_dbFactory, notifications);

        var result = await RunWhileDatabaseLockedAsync(
            () => tracking.ToggleFollowingAsync(2, CancellationToken.None));

        Assert.Equal((expectedStatus, expectedChanged), result);
        Assert.Equal(expectedStatus, await setup.GetStatusAsync(2));
        Assert.Equal(expectedEventCount, (await setup.GetTrackingEventsAsync()).Count);
        Assert.Empty(await new ActionCenterService(_dbFactory).GetPlansAsync(includeArchived: true));
        Assert.Empty(notifications.CancelledGroups);
        if (!expectedChanged)
        {
            var record = Assert.Single(await setup.GetAllTrackingAsync());
            Assert.Equal((2, AnimeTrackingStatus.Watching, "2026-01-02T00:00:00Z"), record);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveStatus_ReturnsWhileSqliteIsLocked_AndKeepsNotificationAndAdmissionWhenRecordIsMissing(
        bool exists)
    {
        var setup = new TrackingService(_dbFactory);
        if (exists)
        {
            await setup.SetStatusWithTimestampAsync(
                2, AnimeTrackingStatus.PlanToWatch, "2026-01-02T00:00:00Z");
        }
        var plans = new ActionCenterService(_dbFactory);
        await plans.UpsertPlanAsync(2, "Synthetic plan", AnimePlanPriority.Normal, null, 0);
        var notifications = new GatedNotifications();
        var tracking = new TrackingService(_dbFactory, notifications);
        using var blocker = await LockDatabaseAsync();
        var ui = new ObservedPausedUiContext();
        var invocation = CallFromPausedContextThread(() => tracking.RemoveStatusAsync(2), ui);
        Task operation;
        try
        {
            operation = await invocation.Returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);
            Assert.Empty(notifications.CancelledGroups);

            blocker.Dispose();
            await ui.Posted.WaitAsync(CompletionBudget);

            Assert.Empty(notifications.CancelledGroups);
            Assert.Null(await setup.GetStatusAsync(2));
            Assert.NotNull((await plans.GetPlanAsync(2))?.ArchivedAt);
            Assert.Empty(await setup.GetTrackingEventsAsync());

            using var exclusive = _dbFactory.Access.BeginExclusive();
            var drained = exclusive.WaitForDrainAsync(CancellationToken.None);
            Assert.False(drained.IsCompleted);

            ui.Resume();
            await notifications.Called.WaitAsync(CompletionBudget);

            Assert.Equal(new[] { "anime-plan-2" }, notifications.CancelledGroups);
            Assert.False(operation.IsCompleted);
            Assert.False(drained.IsCompleted);

            notifications.Release();
            await operation.WaitAsync(CompletionBudget);
            await drained.WaitAsync(CompletionBudget);
            Assert.Equal(exists, await (Task<bool>)operation);
        }
        finally
        {
            blocker.Dispose();
            notifications.Release();
            ui.Resume();
            await FinishInvocationAsync(invocation);
        }
    }

    [Fact]
    public async Task LoadDragZones_ReturnsWhileSqliteIsLocked_AndPreservesEveryFieldAndOrder()
    {
        await SeedConfigAsync("drag_zones", """
            [
              {"id":"second","label":"Right","xPercent":0.73,"yPercent":0.18,
               "widthPercent":0.21,"heightPercent":0.42,"action":4},
              {"id":"first","label":"Left","xPercent":0.12,"yPercent":0.34,
               "widthPercent":0.56,"heightPercent":0.67,"action":1}
            ]
            """);
        var stateStore = _stateStore;

        var zones = await RunWhileDatabaseLockedAsync(stateStore.LoadDragZoneConfigAsync);

        Assert.Collection(zones,
            zone => Assert.Equal(("second", "Right", 0.73, 0.18, 0.21, 0.42, DragAction.Following),
                (zone.Id, zone.Label, zone.XPercent, zone.YPercent, zone.WidthPercent, zone.HeightPercent, zone.Action)),
            zone => Assert.Equal(("first", "Left", 0.12, 0.34, 0.56, 0.67, DragAction.Watching),
                (zone.Id, zone.Label, zone.XPercent, zone.YPercent, zone.WidthPercent, zone.HeightPercent, zone.Action)));
    }

    [Fact]
    public async Task LoadCalendarPick_ReturnsWhileSqliteIsLocked_AndPreservesDateAndAnimeId()
    {
        await SeedConfigAsync("calendar_daily_pick", """{"date":"2026-10-26","animeId":7}""");
        var stateStore = _stateStore;

        var pick = await RunWhileDatabaseLockedAsync(stateStore.LoadCalendarDailyPickAsync);

        (DateOnly Date, int AnimeId)? expected = (new DateOnly(2026, 10, 26), 7);
        Assert.Equal(expected, pick);
    }

    [Fact]
    public async Task LoadTimeMachine_ReturnsWhileSqliteIsLocked_AndMergesValidSeasonsWithoutReorderingIds()
    {
        await SeedConfigAsync("time_machine_shown", """
            [
              {"year":2024,"season":"Winter","animeIds":[9,4,9]},
              {"year":2023,"season":"Summer","animeIds":[7]},
              {"year":2024,"season":"invalid-season","animeIds":[888]},
              {"year":2024,"season":"Winter","animeIds":[4,6]},
              {"year":2023,"season":"Summer","animeIds":null}
            ]
            """);
        var stateStore = _stateStore;

        var shown = await RunWhileDatabaseLockedAsync(stateStore.LoadTimeMachineShownAsync);

        Assert.Equal(2, shown.Count);
        Assert.Equal(new[] { 9, 4, 6 }, shown[(2024, Season.Winter)]);
        Assert.Equal(new[] { 7 }, shown[(2023, Season.Summer)]);
    }

    [Fact]
    public async Task LoadTodayTheme_ReturnsWhileSqliteIsLocked_AndPreservesThemeFallbackAndAllIds()
    {
        await SeedConfigAsync("today_theme", """
            {"date":"2026-10-26","theme":"OneYearAgoToday","fallback":true,"animeIds":[5,3,5]}
            """);
        var stateStore = _stateStore;

        var state = await RunWhileDatabaseLockedAsync(stateStore.LoadTodayThemeAsync);

        Assert.NotNull(state);
        Assert.Equal(new DateOnly(2026, 10, 26), state.Date);
        Assert.Equal(TodayThemeKind.OneYearAgoToday, state.Theme);
        Assert.True(state.IsFallback);
        Assert.Equal(new[] { 5, 3, 5 }, state.AnimeIds);
    }

    [Fact]
    public async Task LoadRecentSearches_ReturnsWhileSqliteIsLocked_AndOnlyDropsBlankValues()
    {
        await SeedConfigAsync("search_recent", """["new"," ","old","new",null,""]""");
        var stateStore = _stateStore;

        var recent = await RunWhileDatabaseLockedAsync(stateStore.LoadRecentSearchesAsync);

        Assert.Equal(new[] { "new", "old", "new" }, recent);
    }

    [Fact]
    public async Task GetTrackingEvents_ReturnsWhileSqliteIsLocked_AndKeepsAllValidDatesWithoutSorting()
    {
        using (var connection = await _dbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO tracking_events(EventId, AnimeId, PreviousStatus, NewStatus, ChangedAt)
                VALUES
                  ('late',12,NULL,@watching,'2026-01-09T03:00:00Z'),
                  ('invalid',18,NULL,@completed,'not-a-date'),
                  ('early',3,NULL,@completed,'2026-01-02T08:00:00+08:00'),
                  ('blank',27,NULL,@watching,''),
                  ('middle',9,NULL,@watching,'2026-01-05T02:00:00-03:00')
                """;
            command.Parameters.AddWithValue("@watching", (int)AnimeTrackingStatus.Watching);
            command.Parameters.AddWithValue("@completed", (int)AnimeTrackingStatus.Completed);
            await command.ExecuteNonQueryAsync();
        }
        var tracking = new TrackingService(_dbFactory);

        var events = await RunWhileDatabaseLockedAsync(() => tracking.GetTrackingEventsAsync());

        Assert.Equal(new[]
        {
            (12, AnimeTrackingStatus.Watching, new DateTimeOffset(2026, 1, 9, 3, 0, 0, TimeSpan.Zero)),
            (3, AnimeTrackingStatus.Completed, new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.FromHours(8))),
            (9, AnimeTrackingStatus.Watching, new DateTimeOffset(2026, 1, 5, 2, 0, 0, TimeSpan.FromHours(-3))),
        }, events);
        Assert.Equal(TimeSpan.FromHours(8), events[1].ChangedAt.Offset);
        Assert.Equal(TimeSpan.FromHours(-3), events[2].ChangedAt.Offset);
    }

    [Fact]
    public async Task ConfigLoaders_PreserveTheirDifferentMalformedJsonAndInvalidValueResults()
    {
        foreach (var key in new[] { "drag_zones", "calendar_daily_pick", "time_machine_shown", "today_theme", "search_recent" })
        {
            await SeedConfigAsync(key, "{");
        }
        var stateStore = _stateStore;

        await Assert.ThrowsAsync<JsonException>(() => stateStore.LoadDragZoneConfigAsync());
        Assert.Null(await stateStore.LoadCalendarDailyPickAsync());
        Assert.Empty(await stateStore.LoadTimeMachineShownAsync());
        Assert.Null(await stateStore.LoadTodayThemeAsync());
        Assert.Empty(await stateStore.LoadRecentSearchesAsync());

        await SeedConfigAsync("drag_zones", "null");
        Assert.Equal(new[] { "tl", "tr", "bl", "br" },
            (await stateStore.LoadDragZoneConfigAsync()).Select(zone => zone.Id));
        await SeedConfigAsync("calendar_daily_pick", """{"date":"2026-02-30","animeId":7}""");
        Assert.Null(await stateStore.LoadCalendarDailyPickAsync());
        await SeedConfigAsync("today_theme", """{"date":"2026-10-26","theme":"Unknown","animeIds":[5]}""");
        Assert.Null(await stateStore.LoadTodayThemeAsync());
    }

    private async Task SeedConfigAsync(string key, string json)
    {
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO config(Key, Value) VALUES(@key, @json)";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@json", json);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedOrderedTrackingAsync(TrackingService tracking)
    {
        await tracking.SetStatusWithTimestampAsync(40, AnimeTrackingStatus.Watching, "2026-01-02T00:00:00Z");
        await tracking.SetStatusWithTimestampAsync(10, AnimeTrackingStatus.Completed, "2026-01-05T00:00:00Z");
        await tracking.SetStatusWithTimestampAsync(30, AnimeTrackingStatus.Watching, "2026-01-04T00:00:00Z");
        await tracking.SetStatusWithTimestampAsync(20, AnimeTrackingStatus.Blocked, "2026-01-03T00:00:00Z");
    }

    private async Task<T> RunWhileDatabaseLockedAsync<T>(Func<Task<T>> operationFactory)
    {
        using var blocker = await LockDatabaseAsync();
        var ui = new PausedUiContext();
        var invocation = CallFromPausedContextThread(() => operationFactory(), ui);
        Task operation;
        try
        {
            operation = await invocation.Returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            blocker.Dispose();
            ui.Resume();
            await FinishInvocationAsync(invocation);
        }

        return await (Task<T>)operation;
    }

    private Task<SqliteConnection> LockDatabaseAsync() => _database.LockExclusiveAsync();

    private static ContextInvocation CallFromPausedContextThread(Func<Task> call, SynchronizationContext context)
    {
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                returned.SetResult(call());
            }
#pragma warning disable CA1031 // 专用调用线程把原始失败交还测试，不允许未观察异常退出进程。
            catch (Exception error)
            {
                returned.SetException(error);
            }
#pragma warning restore CA1031
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
                exited.SetResult();
            }
        }) { IsBackground = true };
        thread.Start();
        return new ContextInvocation(returned.Task, exited.Task);
    }

    private static async Task FinishInvocationAsync(ContextInvocation invocation)
    {
        var operation = await invocation.Returned.WaitAsync(CompletionBudget);
        await Task.WhenAny(operation).WaitAsync(CompletionBudget);
        await operation.ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        await invocation.Exited.WaitAsync(CompletionBudget);
    }

    private sealed record ContextInvocation(Task<Task> Returned, Task Exited);

    private sealed class ObservedPausedUiContext : SynchronizationContext
    {
        private readonly PausedUiContext _paused = new();
        private readonly TaskCompletionSource _posted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _paused.Post(callback, state);
            _posted.TrySetResult();
        }

        internal void Resume() => _paused.Resume();
    }

    private sealed class GatedNotifications : IAppNotificationService
    {
        private readonly TaskCompletionSource _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsSupported => true;
        public bool NotificationsEnabled => true;
        internal List<string> CancelledGroups { get; } = [];
        internal Task Called => _called.Task;

        internal void Release() => _release.TrySetResult();

        public Task CancelGroupAsync(string group, CancellationToken cancellationToken = default)
        {
            CancelledGroups.Add(group);
            _called.TrySetResult();
            return _release.Task;
        }

        public Task ScheduleAsync(AppNotificationRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CancelAsync(string group, string tag, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task OpenNotificationSettingsAsync() => Task.CompletedTask;

        public IDisposable RegisterActivationHandler(
            string category, Func<AppNotificationActivation, CancellationToken, Task> handler)
            => new Registration();

        private sealed class Registration : IDisposable
        {
            public void Dispose() { }
        }
    }
}
