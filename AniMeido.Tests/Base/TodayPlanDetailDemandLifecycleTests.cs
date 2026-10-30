using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed partial class TodayPlanDetailDemandTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Expand_CompletesRemainingDetailsAndReusesTheSamePlanCacheOnReload()
    {
        using var fixture = await CreatePlanFixtureAsync(12);
        var vm = fixture.ViewModel;
        var plans = vm.Plans;
        Assert.Equal(Enumerable.Range(1, 6), fixture.Source.DetailRequests.Order());

        await vm.SetPlanDeckExpandedAsync(true);
        await vm.SetPlanDeckExpandedAsync(false);
        await vm.SetPlanDeckExpandedAsync(true);
        await vm.LoadAsync();
        await vm.Theme.PendingLoad;

        Assert.Same(plans, vm.Plans);
        Assert.Equal(Enumerable.Range(1, 12), vm.Plans.Select(entry => entry.Plan.AnimeId));
        Assert.All(vm.Plans, entry => Assert.Equal(entry.Plan.AnimeId, entry.Anime?.ID));
        Assert.Equal(Enumerable.Range(1, 12), fixture.Source.DetailRequests.Order());
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task ConcurrentExpand_OnlyStartsEachMissingDetailOnce()
    {
        using var fixture = await CreatePlanFixtureAsync(12);
        var vm = fixture.ViewModel;
        var started = Signal();
        var release = Signal();
        fixture.Source.Detail = async (id, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return PlanAnime(id);
        };
        var first = vm.SetPlanDeckExpandedAsync(true);
        Task second = Task.CompletedTask;
        Task third = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(Budget);
            second = vm.SetPlanDeckExpandedAsync(true);
            third = vm.SetPlanDeckExpandedAsync(true);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(third.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second, third).WaitAsync(Budget);
        }

        Assert.Equal(Enumerable.Range(1, 12), fixture.Source.DetailRequests.Order());
        Assert.All(vm.Plans, entry => Assert.Equal(entry.Plan.AnimeId, entry.Anime?.ID));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollapseOrLeave_CancelsRearDemandAndRejectsLateDetailsUntilReopened(bool leavePage)
    {
        using var fixture = await CreatePlanFixtureAsync(12);
        var vm = fixture.ViewModel;
        var started = Signal();
        var release = Signal();
        var requestTokens = new ConcurrentQueue<CancellationToken>();
        fixture.Source.Detail = async (id, token) =>
        {
            requestTokens.Enqueue(token);
            started.TrySetResult();
            // 模拟取消已经发出后，远端仍返回旧结果。
            await release.Task;
            return PlanAnime(id, "旧后排详情");
        };
        var expanded = vm.SetPlanDeckExpandedAsync(true);
        try
        {
            await started.Task.WaitAsync(Budget);
            if (leavePage)
                vm.SuspendPlanDetails();
            else
                await vm.SetPlanDeckExpandedAsync(false).WaitAsync(Budget);
            Assert.NotEmpty(requestTokens);
            Assert.All(requestTokens, token => Assert.True(token.IsCancellationRequested));
        }
        finally
        {
            release.TrySetResult();
            await ObserveCancellationAsync(expanded);
        }

        Assert.All(vm.Plans.Take(6), entry => Assert.Equal(entry.Plan.AnimeId, entry.Anime?.ID));
        Assert.All(vm.Plans.Skip(6), entry => Assert.Null(entry.Anime));
        Assert.DoesNotContain(11, fixture.Source.DetailRequests);
        Assert.DoesNotContain(12, fixture.Source.DetailRequests);
        fixture.Source.Detail = (id, _) => Task.FromResult<Anime?>(PlanAnime(id, "当前后排详情"));

        await vm.SetPlanDeckExpandedAsync(true);

        Assert.Equal(12, vm.Plans.Count);
        Assert.All(vm.Plans.Skip(6), entry => Assert.Equal("当前后排详情", entry.Anime?.Description));
        Assert.All(Enumerable.Range(1, 6), id =>
            Assert.Equal(1, fixture.Source.DetailRequests.Count(requested => requested == id)));
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task ReloadWithReorderedAndBlockedPlans_RejectsOldDetailsByIdentityRatherThanOldIndex()
    {
        using var fixture = await CreatePlanFixtureAsync(12);
        var vm = fixture.ViewModel;
        var ninthStarted = Signal();
        var release = Signal();
        fixture.Source.Detail = async (id, _) =>
        {
            if (id == 9)
                ninthStarted.TrySetResult();
            await release.Task;
            return PlanAnime(id, "旧计划集详情");
        };
        var oldExpansion = vm.SetPlanDeckExpandedAsync(true);
        Task replacement = Task.CompletedTask;
        try
        {
            await ninthStarted.Task.WaitAsync(Budget);
            await fixture.Tracking.SetStatusAsync(7, AnimeTrackingStatus.Blocked);
            await fixture.ActionCenter.UpsertPlanAsync(
                9, "计划9", AnimePlanPriority.Critical, DateOnly.FromDateTime(DateTime.Today).AddDays(-1), 0);
            fixture.Source.Detail = (id, _) => Task.FromResult<Anime?>(PlanAnime(id, "新计划集详情"));

            replacement = vm.LoadAsync();
            Assert.False(replacement.IsCompleted);
            release.TrySetResult();
            await replacement.WaitAsync(Budget);
            await vm.Theme.PendingLoad;
            Assert.Equal(9, vm.Plans[0].Plan.AnimeId);
            Assert.Equal("新计划集详情", vm.Plans[0].Anime?.Description);
            Assert.DoesNotContain(vm.Plans, entry => entry.Plan.AnimeId == 7);
        }
        finally
        {
            release.TrySetResult();
            await ObserveCancellationAsync(oldExpansion);
            await replacement.WaitAsync(Budget);
        }

        Assert.Equal(new[] { 9, 1, 2, 3, 4, 5, 6, 8, 10, 11, 12 },
            vm.Plans.Select(entry => entry.Plan.AnimeId));
        Assert.Equal("新计划集详情", vm.Plans[0].Anime?.Description);
        Assert.All(vm.Plans.Where(entry => entry.Anime is not null),
            entry => Assert.Equal(entry.Plan.AnimeId, entry.Anime?.ID));
        Assert.DoesNotContain(vm.Plans, entry => entry.Anime?.Description == "旧计划集详情");
    }

    [Fact]
    public async Task Reopen_WaitsForCancelledRearRequestsToDrainBeforeStartingAnotherFour()
    {
        using var fixture = await CreatePlanFixtureAsync(12);
        var vm = fixture.ViewModel;
        var fourStarted = Signal();
        var releaseOld = Signal();
        var metricsGate = new object();
        var active = 0;
        var peak = 0;
        var oldDemand = true;
        fixture.Source.Detail = async (id, _) =>
        {
            var fromOldDemand = oldDemand;
            lock (metricsGate)
            {
                active++;
                peak = Math.Max(peak, active);
                if (fromOldDemand && active == 4)
                    fourStarted.TrySetResult();
            }
            try
            {
                if (fromOldDemand)
                    await releaseOld.Task;
                return PlanAnime(id, fromOldDemand ? "旧需求" : "当前需求");
            }
            finally
            {
                lock (metricsGate)
                    active--;
            }
        };
        var original = vm.SetPlanDeckExpandedAsync(true);
        Task reopened = Task.CompletedTask;
        try
        {
            await fourStarted.Task.WaitAsync(Budget);
            await vm.SetPlanDeckExpandedAsync(false).WaitAsync(Budget);
            oldDemand = false;
            reopened = vm.SetPlanDeckExpandedAsync(true);
            Assert.False(reopened.IsCompleted);
        }
        finally
        {
            releaseOld.TrySetResult();
            await ObserveCancellationAsync(original);
            await reopened.WaitAsync(Budget);
        }

        Assert.Equal(4, peak);
        Assert.Equal(0, active);
        Assert.All(vm.Plans.Skip(6), entry => Assert.Equal("当前需求", entry.Anime?.Description));
        Assert.All(Enumerable.Range(1, 6), id =>
            Assert.Equal(1, fixture.Source.DetailRequests.Count(requested => requested == id)));
    }

    private async Task<PlanFixture> CreatePlanFixtureAsync(int count)
    {
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        for (var id = 1; id <= count; id++)
            await actionCenter.UpsertPlanAsync(id, $"计划{id}", AnimePlanPriority.Normal, null, id);
        var tracking = new TrackingService(DbFactory);
        var source = new PlanSource();
        var reminders = new PlanReminderCoordinator(actionCenter, new NoopNotifications(), new NoopNavigator());
        var vm = new TodayViewModel(
            source, tracking, FeatureState, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory), NullLogger.Instance);
        var fixture = new PlanFixture(vm, source, actionCenter, tracking, reminders);
        try
        {
            await vm.LoadAsync();
            await vm.Theme.PendingLoad;
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static Anime PlanAnime(int id, string description = "")
        => new(id, $"资料{id}", null, [], null,
            $"https://example.invalid/{id}.jpg", description, 2026, 1);

    private static TaskCompletionSource Signal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Budget);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class PlanFixture(
        TodayViewModel viewModel,
        PlanSource source,
        ActionCenterService actionCenter,
        TrackingService tracking,
        PlanReminderCoordinator reminders) : IDisposable
    {
        public TodayViewModel ViewModel { get; } = viewModel;
        public PlanSource Source { get; } = source;
        public ActionCenterService ActionCenter { get; } = actionCenter;
        public TrackingService Tracking { get; } = tracking;

        public void Dispose()
        {
            ViewModel.SuspendPlanDetails();
            ViewModel.Theme.CancelLoad();
            reminders.Dispose();
        }
    }
}
