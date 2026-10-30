using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

[Collection(CurrentSeasonSessionCollection.Name)]
public sealed class CurrentSeasonDailyPickDetailsTests : DbTestBase
{
    private readonly PersonalFeatureStateStore _featureState;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly DateOnly TestDay = new(2026, 9, 18);

    public CurrentSeasonDailyPickDetailsTests()
    {
        _featureState = new PersonalFeatureStateStore(DbFactory);
        CurrentSeasonViewModel.ResetTimeMachineSession();
    }

    [Fact]
    public async Task PickingAnotherCard_ReplacesTheActualDetailCommandAndRejectsTheOldResult()
    {
        await RunProductionMigrationAsync();
        var source = new DetailSource();
        var vm = CreateViewModel(source);
        Task original = Task.CompletedTask;
        Task replacement = Task.CompletedTask;

        try
        {
            await LoadAsync(vm, source);
            var firstPick = Assert.IsType<CalendarEntry>(vm.DailyPick);
            original = Assert.IsAssignableFrom<Task>(vm.LoadDailyPickDetailsCommand.ExecutionTask);
            var firstRequest = Assert.Single(source.DetailRequests);
            Assert.Equal(firstPick.Anime.ID, firstRequest.Id);
            Assert.False(original.IsCompleted);

            await vm.NextDailyPickCommand.ExecuteAsync(null).WaitAsync(Budget);
            var nextPick = Assert.IsType<CalendarEntry>(vm.DailyPick);
            Assert.NotEqual(firstPick.Anime.ID, nextPick.Anime.ID);
            replacement = Assert.IsAssignableFrom<Task>(vm.LoadDailyPickDetailsCommand.ExecutionTask);
            Assert.NotSame(original, replacement);
            Assert.True(firstRequest.Token.IsCancellationRequested);
            Assert.Equal(new[] { firstPick.Anime.ID, nextPick.Anime.ID }, source.DetailRequests.Select(request => request.Id));

            source.FirstDetail.TrySetResult(Item(firstPick.Anime.ID) with { Description = "旧卡片简介" });
            await original.WaitAsync(Budget);
            Assert.Same(nextPick, vm.DailyPick);
            Assert.Empty(vm.DailyPickDescription);
            Assert.Empty(source.TagRequests);

            source.SecondDetail.TrySetResult(Item(nextPick.Anime.ID) with { Description = " 新卡片简介 " });
            await replacement.WaitAsync(Budget);
            Assert.Equal("新卡片简介", vm.DailyPickDescription);
            Assert.Equal(["奇幻", "治愈"], vm.DailyPickTags);
            Assert.Equal([nextPick.Anime.ID], source.TagRequests);
        }
        finally
        {
            source.Release();
            await StopAndDrainAsync(vm, original, replacement);
        }
    }

    [Fact]
    public async Task ReturningAfterADetailFailure_DoesNotAutomaticallyRetryTheSameCard()
    {
        await RunProductionMigrationAsync();
        var source = new DetailSource();
        var vm = CreateViewModel(source);
        Task details = Task.CompletedTask;

        try
        {
            await LoadAsync(vm, source);
            var pick = Assert.IsType<CalendarEntry>(vm.DailyPick);
            details = Assert.IsAssignableFrom<Task>(vm.LoadDailyPickDetailsCommand.ExecutionTask);
            var request = Assert.Single(source.DetailRequests);
            Assert.Equal(pick.Anime.ID, request.Id);
            Assert.False(request.Token.IsCancellationRequested);
            Assert.False(details.IsCompleted);
            Assert.False(source.SecondDetail.Task.IsCompleted);
            Assert.True(source.FirstDetail.TrySetException(new HttpRequestException("详情暂不可用")));
            await details.WaitAsync(Budget);
            Assert.Empty(vm.DailyPickDescription);
            Assert.Empty(vm.DailyPickTags);

            vm.CancelSupplementaryLoads();
            vm.ResumeSupplementaryLoads();

            Assert.Same(pick, vm.DailyPick);
            Assert.Same(details, vm.LoadDailyPickDetailsCommand.ExecutionTask);
            Assert.Single(source.DetailRequests);
            Assert.Empty(source.TagRequests);
            Assert.False(vm.IsError);
        }
        finally
        {
            source.Release();
            await StopAndDrainAsync(vm, details);
        }
    }

    [Fact]
    public async Task ReturningAfterCancellation_RestartsTheActualDetailCommandForTheSameCard()
    {
        await RunProductionMigrationAsync();
        var source = new DetailSource();
        var vm = CreateViewModel(source);
        Task original = Task.CompletedTask;
        Task resumed = Task.CompletedTask;

        try
        {
            await LoadAsync(vm, source);
            var pick = Assert.IsType<CalendarEntry>(vm.DailyPick);
            original = Assert.IsAssignableFrom<Task>(vm.LoadDailyPickDetailsCommand.ExecutionTask);
            var firstRequest = Assert.Single(source.DetailRequests);
            vm.CancelSupplementaryLoads();
            Assert.True(firstRequest.Token.IsCancellationRequested);
            vm.ResumeSupplementaryLoads();
            resumed = Assert.IsAssignableFrom<Task>(vm.LoadDailyPickDetailsCommand.ExecutionTask);
            Assert.NotSame(original, resumed);
            Assert.Equal(new[] { pick.Anime.ID, pick.Anime.ID }, source.DetailRequests.Select(request => request.Id));

            source.FirstDetail.TrySetResult(Item(pick.Anime.ID) with { Description = "离页前的旧简介" });
            await original.WaitAsync(Budget);
            Assert.Same(pick, vm.DailyPick);
            Assert.Empty(vm.DailyPickDescription);
            Assert.Empty(source.TagRequests);

            source.SecondDetail.TrySetResult(Item(pick.Anime.ID) with { Description = "返回后的简介" });
            await resumed.WaitAsync(Budget);
            Assert.Equal("返回后的简介", vm.DailyPickDescription);
            Assert.Equal(["奇幻", "治愈"], vm.DailyPickTags);
            Assert.Equal([pick.Anime.ID], source.TagRequests);
            Assert.False(vm.IsError);
        }
        finally
        {
            source.Release();
            await StopAndDrainAsync(vm, original, resumed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledDetails_KeepTheTokenResourceAliveUntilTheActualRequestEnds(bool replaceByNextCard)
    {
        await RunProductionMigrationAsync();
        var source = new DetailSource { ProbeFirstTokenAfterRelease = true };
        var vm = CreateViewModel(source);
        Task original = Task.CompletedTask;
        Task replacement = Task.CompletedTask;

        try
        {
            await LoadAsync(vm, source);
            var firstPick = Assert.IsType<CalendarEntry>(vm.DailyPick);
            original = Assert.IsAssignableFrom<Task>(vm.LoadDailyPickDetailsCommand.ExecutionTask);
            var firstRequest = Assert.Single(source.DetailRequests);
            if (replaceByNextCard)
            {
                await vm.NextDailyPickCommand.ExecuteAsync(null).WaitAsync(Budget);
            }
            else
            {
                vm.CancelSupplementaryLoads();
                vm.ResumeSupplementaryLoads();
            }
            var currentPick = Assert.IsType<CalendarEntry>(vm.DailyPick);
            if (replaceByNextCard)
                Assert.NotEqual(firstPick.Anime.ID, currentPick.Anime.ID);
            else
                Assert.Same(firstPick, currentPick);
            replacement = Assert.IsAssignableFrom<Task>(vm.LoadDailyPickDetailsCommand.ExecutionTask);
            Assert.NotSame(original, replacement);
            Assert.True(firstRequest.Token.IsCancellationRequested);
            Assert.Equal(2, source.DetailRequests.Count);

            source.FirstDetail.TrySetResult(Item(firstPick.Anime.ID) with { Description = "旧请求的简介" });
            var probe = await source.FirstTokenProbe.Task.WaitAsync(Budget);
            Assert.False(original.IsCompleted);
            Assert.Null(probe.Error);
            Assert.True(probe.CancellationSignaled);
            Assert.Same(currentPick, vm.DailyPick);
            Assert.Empty(vm.DailyPickDescription);
            Assert.Empty(vm.DailyPickTags);
            Assert.Empty(source.TagRequests);

            source.FinishFirstTokenProbe();
            await original.WaitAsync(Budget);
            Assert.Throws<ObjectDisposedException>(() =>
            {
                _ = firstRequest.Token.WaitHandle;
            });
            Assert.Same(currentPick, vm.DailyPick);
            Assert.Empty(vm.DailyPickDescription);
            Assert.Empty(source.TagRequests);

            source.SecondDetail.TrySetResult(Item(currentPick.Anime.ID) with { Description = "当前请求的简介" });
            await replacement.WaitAsync(Budget);
            Assert.Equal("当前请求的简介", vm.DailyPickDescription);
            Assert.Equal(["奇幻", "治愈"], vm.DailyPickTags);
            Assert.Equal([currentPick.Anime.ID], source.TagRequests);
            var currentRequest = source.DetailRequests.Last();
            Assert.Throws<ObjectDisposedException>(() =>
            {
                _ = currentRequest.Token.WaitHandle;
            });
        }
        finally
        {
            source.Release();
            await StopAndDrainAsync(vm, original, replacement);
        }
    }

    private CurrentSeasonViewModel CreateViewModel(DetailSource source)
        => new(source, new TrackingService(DbFactory), _featureState, new CalendarTestTime(TestDay), NullLogger.Instance);

    private static async Task LoadAsync(CurrentSeasonViewModel vm, DetailSource source)
    {
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null).WaitAsync(Budget);
        // 先完成主流程选卡，再交回季度结果；本组不把初始化的并发选卡当成被测行为。
        source.ReleaseCurrentSeason();
        await Task.WhenAll(vm.PendingOthersLoad, vm.PendingTimeMachineLoad).WaitAsync(Budget);
        await vm.PendingTimeMachineShownSave.WaitAsync(Budget);
    }

    private static async Task StopAndDrainAsync(CurrentSeasonViewModel vm, params Task[] capturedDetails)
    {
        vm.LoadSeasonalAnimeCommand.Cancel();
        vm.CancelSupplementaryLoads();
        await Task.WhenAll(capturedDetails).WaitAsync(Budget);
        await Task.WhenAll(
            vm.LoadDailyPickDetailsCommand.ExecutionTask ?? Task.CompletedTask,
            vm.LoadSeasonalAnimeCommand.ExecutionTask ?? Task.CompletedTask,
            vm.NextDailyPickCommand.ExecutionTask ?? Task.CompletedTask,
            vm.PendingOthersLoad,
            vm.PendingTimeMachineLoad).WaitAsync(Budget);
        await vm.PendingTimeMachineShownSave.WaitAsync(Budget);
    }

    private static Anime Item(int id)
        => new(id, $"作品{id}", null, [], new DateOnly(2026, 7, 3), null, "", 2026, 7,
            AnimeListPresentation.ToBangumiWeekday(TestDay.DayOfWeek), 7,
            MediaFormat: AnimeMediaFormat.Television);

    private sealed class DetailSource : IAnimeDataSource
    {
        private int _detailCalls;
        private readonly TaskCompletionSource<List<Anime>> _currentSeason = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finishFirstTokenProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ProbeFirstTokenAfterRelease { get; init; }
        public TaskCompletionSource<(bool CancellationSignaled, Exception? Error)> FirstTokenProbe { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Anime?> FirstDetail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Anime?> SecondDetail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(int Id, CancellationToken Token)> DetailRequests { get; } = new();
        public ConcurrentQueue<int> TagRequests { get; } = new();

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => Task.FromResult(new List<Anime> { Item(1), Item(2) });
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => year == TestDay.Year && season == SeasonHelper.FromMonth(TestDay.Month)
                ? _currentSeason.Task : Task.FromResult(new List<Anime>());

        public void ReleaseCurrentSeason() => _currentSeason.TrySetResult([]);

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
        {
            DetailRequests.Enqueue((animeID, ct));
            if (Interlocked.Increment(ref _detailCalls) != 1)
                return SecondDetail.Task;
            return ProbeFirstTokenAfterRelease ? ReadFirstDetailAndProbeTokenAsync(ct) : FirstDetail.Task;
        }

        private async Task<Anime?> ReadFirstDetailAndProbeTokenAsync(CancellationToken token)
        {
            var detail = await FirstDetail.Task;
            var signaled = false;
            Exception? error = null;
            try
            {
                signaled = token.WaitHandle.WaitOne(0);
            }
            catch (ObjectDisposedException ex)
            {
                // 详情失败会降级为留空，假网络必须显式交回资源访问结果，避免测试假绿。
                error = ex;
            }
            FirstTokenProbe.TrySetResult((signaled, error));
            await _finishFirstTokenProbe.Task;
            return detail;
        }

        public void FinishFirstTokenProbe() => _finishFirstTokenProbe.TrySetResult();

        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
        {
            TagRequests.Enqueue(animeID);
            return Task.FromResult(new List<Tag> { new("季度"), new("奇幻"), new("治愈") });
        }

        public void Release()
        {
            ReleaseCurrentSeason();
            FinishFirstTokenProbe();
            FirstDetail.TrySetResult(null);
            SecondDetail.TrySetResult(null);
        }

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
