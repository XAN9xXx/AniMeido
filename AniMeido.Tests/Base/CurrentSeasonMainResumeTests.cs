using AniMeido.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

[Collection(CurrentSeasonSessionCollection.Name)]
public sealed class CurrentSeasonMainResumeTests : DbTestBase
{
    private readonly PersonalFeatureStateStore _featureState;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly DateOnly TestDay = new(2026, 9, 18);

    public CurrentSeasonMainResumeTests()
    {
        _featureState = new PersonalFeatureStateStore(DbFactory);
        CurrentSeasonViewModel.ResetTimeMachineSession();
    }

    [Fact]
    public async Task Resume_WhenMainCommandIsRunningWithoutCancellation_DoesNotStartAnotherRequest()
    {
        await RunProductionMigrationAsync();
        var schedule = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScheduleSource { ReadSchedule = _ => schedule.Task };
        var vm = CreateViewModel(source);
        var loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);

        try
        {
            await source.ScheduleEntered.Task.WaitAsync(Budget);
            Assert.True(vm.LoadSeasonalAnimeCommand.IsRunning);
            Assert.False(vm.LoadSeasonalAnimeCommand.IsCancellationRequested);

            await vm.ResumeMainLoadCommand.ExecuteAsync(null).WaitAsync(Budget);
            vm.ResumeSupplementaryLoads();

            Assert.Equal(1, source.ScheduleCalls);
            Assert.Same(loading, vm.LoadSeasonalAnimeCommand.ExecutionTask);
            Assert.False(loading.IsCompleted);
            schedule.TrySetResult([Item(1)]);
            await loading.WaitAsync(Budget);
            Assert.True(vm.HasData);
        }
        finally
        {
            schedule.TrySetResult([]);
            await StopAndDrainAsync(vm);
        }
    }

    [Fact]
    public async Task Resume_WhenPublishedDataExists_KeepsMainResultsWithoutReloading()
    {
        await RunProductionMigrationAsync();
        var source = new ScheduleSource { ReadSchedule = _ => Task.FromResult(new List<Anime> { Item(1) }) };
        var vm = CreateViewModel(source);

        try
        {
            await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null).WaitAsync(Budget);
            await DrainSupplementaryAsync(vm);
            Assert.True(vm.HasData);
            var entry = Assert.Single(vm.VisibleEntries);
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();

            await vm.ResumeMainLoadCommand.ExecuteAsync(null).WaitAsync(Budget);
            vm.ResumeSupplementaryLoads();

            Assert.Equal(1, source.ScheduleCalls);
            Assert.Same(entry, Assert.Single(vm.VisibleEntries));
            Assert.False(vm.IsError);
        }
        finally
        {
            await StopAndDrainAsync(vm);
        }
    }

    [Fact]
    public async Task Resume_WhenMainLoadFailed_DoesNotAutomaticallyRetryOrClearTheError()
    {
        await RunProductionMigrationAsync();
        var source = new ScheduleSource
        {
            ReadSchedule = _ => Task.FromException<List<Anime>>(new HttpRequestException("仍需手动重试")),
        };
        var vm = CreateViewModel(source);

        try
        {
            await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null).WaitAsync(Budget);
            Assert.True(vm.IsError);
            Assert.False(vm.HasData);
            var error = vm.ErrorMessage;
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();

            await vm.ResumeMainLoadCommand.ExecuteAsync(null).WaitAsync(Budget);
            vm.ResumeSupplementaryLoads();

            Assert.Equal(1, source.ScheduleCalls);
            Assert.True(vm.IsError);
            Assert.Equal(error, vm.ErrorMessage);
            Assert.False(vm.HasData);
        }
        finally
        {
            await StopAndDrainAsync(vm);
        }
    }

    [Fact]
    public async Task Resume_AfterSuccessfulEmptyResults_ReloadsOnEachReturnAsBefore()
    {
        await RunProductionMigrationAsync();
        var source = new ScheduleSource();
        var vm = CreateViewModel(source);

        try
        {
            await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null).WaitAsync(Budget);
            await DrainSupplementaryAsync(vm);
            Assert.False(vm.HasData);
            Assert.False(vm.IsError);

            for (var expectedCalls = 2; expectedCalls <= 3; expectedCalls++)
            {
                vm.LoadSeasonalAnimeCommand.Cancel();
                vm.CancelSupplementaryLoads();
                await vm.ResumeMainLoadCommand.ExecuteAsync(null).WaitAsync(Budget);
                vm.ResumeSupplementaryLoads();
                await (vm.LoadSeasonalAnimeCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(Budget);
                await DrainSupplementaryAsync(vm);

                Assert.Equal(expectedCalls, source.ScheduleCalls);
                Assert.False(vm.HasData);
                Assert.False(vm.IsError);
                Assert.Empty(vm.VisibleEntries);
            }
        }
        finally
        {
            await StopAndDrainAsync(vm);
        }
    }

    [Fact]
    public async Task Resume_AfterCancellationWaitsForTheOldRequestAndStartsOneFreshMainLoad()
    {
        await RunProductionMigrationAsync();
        var oldSchedule = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var source = new ScheduleSource
        {
            ReadSchedule = _ => Interlocked.Increment(ref reads) == 1
                ? oldSchedule.Task : Task.FromResult(new List<Anime> { Item(2) }),
        };
        var vm = CreateViewModel(source);
        var loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Task resuming = Task.CompletedTask;

        try
        {
            await source.ScheduleEntered.Task.WaitAsync(Budget);
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();
            resuming = vm.ResumeMainLoadCommand.ExecuteAsync(null);
            vm.ResumeSupplementaryLoads();
            Assert.Equal(1, source.ScheduleCalls);

            oldSchedule.TrySetResult([Item(99)]);
            await loading.WaitAsync(Budget);
            await resuming.WaitAsync(Budget);
            Assert.Equal(2, source.ScheduleCalls);
            await (vm.LoadSeasonalAnimeCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(Budget);
            await DrainSupplementaryAsync(vm);
            Assert.True(vm.HasData);
            Assert.Equal(2, Assert.Single(vm.VisibleEntries).Anime.ID);
            Assert.False(vm.IsError);
        }
        finally
        {
            oldSchedule.TrySetResult([]);
            await resuming.WaitAsync(Budget);
            await StopAndDrainAsync(vm);
        }
    }

    [Fact]
    public async Task Resume_TwoWaitersDoNotReloadAgainAfterTheReplacementReturnsAnEmptyResult()
    {
        await RunProductionMigrationAsync();
        var oldSchedule = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var source = new ScheduleSource
        {
            ReadSchedule = _ => Interlocked.Increment(ref reads) == 1
                ? oldSchedule.Task : Task.FromResult(new List<Anime>()),
        };
        var vm = CreateViewModel(source);
        var pausedReturn = new PausedUiContext();
        var loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Task firstReturn = Task.CompletedTask;
        Task secondReturn = Task.CompletedTask;

        try
        {
            await source.ScheduleEntered.Task.WaitAsync(Budget);
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();
            vm.ResumeMainLoadCommand.Execute(null);
            firstReturn = Assert.IsAssignableFrom<Task>(vm.ResumeMainLoadCommand.ExecutionTask);
            var previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(pausedReturn);
                vm.ResumeMainLoadCommand.Execute(null);
                secondReturn = Assert.IsAssignableFrom<Task>(vm.ResumeMainLoadCommand.ExecutionTask);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            vm.ResumeSupplementaryLoads();

            oldSchedule.TrySetResult([]);
            await loading.WaitAsync(Budget);
            await firstReturn.WaitAsync(Budget);
            Assert.Equal(2, source.ScheduleCalls);
            await (vm.LoadSeasonalAnimeCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(Budget);
            Assert.False(vm.LoadSeasonalAnimeCommand.IsRunning);
            Assert.False(vm.HasData);
            await pausedReturn.FirstPost.WaitAsync(Budget);
            Assert.False(secondReturn.IsCompleted);

            pausedReturn.Resume();
            await secondReturn.WaitAsync(Budget);
            Assert.Equal(2, source.ScheduleCalls);
            Assert.False(vm.HasData);
            Assert.False(vm.IsError);
        }
        finally
        {
            oldSchedule.TrySetResult([]);
            pausedReturn.Resume();
            await Task.WhenAll(loading, firstReturn, secondReturn).WaitAsync(Budget);
            await StopAndDrainAsync(vm);
        }
    }

    [Fact]
    public async Task Resume_WhenThePageLeavesAgainWhileWaiting_DoesNotRestartUntilAnotherReturn()
    {
        await RunProductionMigrationAsync();
        var oldSchedule = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var source = new ScheduleSource
        {
            ReadSchedule = _ => Interlocked.Increment(ref reads) == 1
                ? oldSchedule.Task : Task.FromResult(new List<Anime> { Item(2) }),
        };
        var vm = CreateViewModel(source);
        var loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Task resuming = Task.CompletedTask;

        try
        {
            await source.ScheduleEntered.Task.WaitAsync(Budget);
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();
            resuming = vm.ResumeMainLoadCommand.ExecuteAsync(null);
            vm.ResumeSupplementaryLoads();
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();

            oldSchedule.TrySetResult([]);
            await Task.WhenAll(loading, resuming).WaitAsync(Budget);
            Assert.Equal(1, source.ScheduleCalls);
            Assert.False(vm.HasData);
            Assert.False(vm.IsError);

            await vm.ResumeMainLoadCommand.ExecuteAsync(null).WaitAsync(Budget);
            vm.ResumeSupplementaryLoads();
            await (vm.LoadSeasonalAnimeCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(Budget);
            Assert.Equal(2, source.ScheduleCalls);
            Assert.Equal(2, Assert.Single(vm.VisibleEntries).Anime.ID);
        }
        finally
        {
            oldSchedule.TrySetResult([]);
            await resuming.WaitAsync(Budget);
            await StopAndDrainAsync(vm);
        }
    }

    [Fact]
    public async Task Resume_AnUnknownOldFailureRemainsWithTheOriginalTaskOwner()
    {
        await RunProductionMigrationAsync();
        var oldSchedule = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new FormatException("原始主任务保留此异常");
        var source = new ScheduleSource { ReadSchedule = _ => oldSchedule.Task };
        var vm = CreateViewModel(source);
        var loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Task resuming = Task.CompletedTask;

        try
        {
            await source.ScheduleEntered.Task.WaitAsync(Budget);
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();
            resuming = vm.ResumeMainLoadCommand.ExecuteAsync(null);
            vm.ResumeSupplementaryLoads();

            oldSchedule.TrySetException(error);
            var observed = await Assert.ThrowsAsync<FormatException>(() => loading.WaitAsync(Budget));
            Assert.Same(error, observed);
            await resuming.WaitAsync(Budget);
            Assert.Equal(1, source.ScheduleCalls);
            Assert.False(vm.HasData);
            Assert.False(vm.IsError);
        }
        finally
        {
            oldSchedule.TrySetException(error);
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();
            try
            {
                await loading.WaitAsync(Budget);
            }
            catch (FormatException ex) when (ReferenceEquals(ex, error))
            {
            }
            await resuming.WaitAsync(Budget);
            await DrainSupplementaryAsync(vm);
        }
    }

    private CurrentSeasonViewModel CreateViewModel(ScheduleSource source)
        => new(source, new TrackingService(DbFactory), _featureState, new CalendarTestTime(TestDay), NullLogger.Instance);

    private static async Task DrainSupplementaryAsync(CurrentSeasonViewModel vm)
    {
        await Task.WhenAll(vm.PendingOthersLoad, vm.PendingTimeMachineLoad).WaitAsync(Budget);
        await vm.PendingTimeMachineShownSave.WaitAsync(Budget);
    }

    private static async Task StopAndDrainAsync(CurrentSeasonViewModel vm)
    {
        vm.LoadSeasonalAnimeCommand.Cancel();
        vm.CancelSupplementaryLoads();
        await Task.WhenAll(
            vm.LoadSeasonalAnimeCommand.ExecutionTask ?? Task.CompletedTask,
            vm.ResumeMainLoadCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(Budget);
        await DrainSupplementaryAsync(vm);
    }

    private static Anime Item(int id)
        => new(id, $"作品{id}", null, [], new DateOnly(2026, 7, 3), null, "", 2026, 7,
            AnimeListPresentation.ToBangumiWeekday(TestDay.DayOfWeek), 7,
            MediaFormat: AnimeMediaFormat.Television);

    private sealed class ScheduleSource : IAnimeDataSource
    {
        private int _scheduleCalls;
        public int ScheduleCalls => Volatile.Read(ref _scheduleCalls);
        public TaskCompletionSource ScheduleEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task<List<Anime>>> ReadSchedule { get; init; }
            = _ => Task.FromResult(new List<Anime>());

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _scheduleCalls);
            ScheduleEntered.TrySetResult();
            return ReadSchedule(ct);
        }

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => Task.FromResult(new List<Anime>());
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
            => Task.FromResult<Anime?>(Item(animeID));
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => Task.FromResult(new List<Tag>());
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Studio>>>();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<VoiceActor>>>();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<CharacterRole>>>();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Unexpected<Task<List<PersonWork>>>();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
            CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
            => Unexpected<Task<(List<Anime>, int)>>();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(
            string keyword, int offset, CancellationToken ct)
            => Unexpected<Task<(List<Anime>, int, int)>>();

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");
    }
}
