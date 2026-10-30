using AniMeido.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

[Collection(CurrentSeasonSessionCollection.Name)]
public sealed class CurrentSeasonSupplementaryLifetimeTests : DbTestBase
{
    private readonly PersonalFeatureStateStore _featureState;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly DateOnly TestDay = new(2026, 9, 18);

    public CurrentSeasonSupplementaryLifetimeTests()
    {
        _featureState = new PersonalFeatureStateStore(DbFactory);
        CurrentSeasonViewModel.ResetTimeMachineSession();
    }

    public enum SupplementaryLoad
    {
        Others,
        TimeMachine,
    }

    [Theory]
    [InlineData(SupplementaryLoad.Others)]
    [InlineData(SupplementaryLoad.TimeMachine)]
    public async Task Cancellation_KeepsTokenResourcesUntilTheActualSupplementaryRequestFinishes(
        SupplementaryLoad loadKind)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        // 唯一周更作品已关注，不额外发起今日一抽详情。
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Following);
        var source = new TokenUsingSeasonSource(loadKind);
        var vm = new CurrentSeasonViewModel(source, tracking, _featureState, new CalendarTestTime(TestDay), NullLogger.Instance);
        Task? main = null;
        Task? requested = null;

        try
        {
            if (loadKind == SupplementaryLoad.Others)
            {
                main = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
                await source.Entered.Task.WaitAsync(Budget);
                await main.WaitAsync(Budget);
                requested = vm.PendingOthersLoad;
            }
            else
            {
                requested = vm.LoadTimeMachineAsync();
                await source.Entered.Task.WaitAsync(Budget);
            }
            Assert.False(requested.IsCompleted);

            vm.CancelSupplementaryLoads();
            Assert.True(source.ObservedToken.IsCancellationRequested);
            source.Probe.SetResult();

            Assert.True(await source.HandleWasSignaled.Task.WaitAsync(Budget));
            Assert.False(requested.IsCompleted);

            source.Finish.SetResult();
            await requested.WaitAsync(Budget);
        }
        finally
        {
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();
            source.Probe.TrySetResult();
            source.Finish.TrySetResult();
            await Task.WhenAll(main ?? Task.CompletedTask, requested ?? Task.CompletedTask,
                vm.PendingOthersLoad, vm.PendingTimeMachineLoad, vm.PendingTimeMachineShownSave)
                .WaitAsync(Budget);
            if (source.HandleWasSignaled.Task.IsCompleted)
            {
                await ((Task)source.HandleWasSignaled.Task).ConfigureAwait(
                    ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }
    }

    private sealed class TokenUsingSeasonSource(SupplementaryLoad loadKind) : IAnimeDataSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Probe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> HandleWasSignaled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ObservedToken { get; private set; }

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => Task.FromResult(new List<Anime>
            {
                new(1, "周更作品", null, [], TestDay, null, "", TestDay.Year, 7,
                    AnimeListPresentation.ToBangumiWeekday(TestDay.DayOfWeek), 7,
                    MediaFormat: AnimeMediaFormat.Television),
            });

        public async Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            var selected = loadKind == SupplementaryLoad.Others
                ? year == TestDay.Year
                : year == TestDay.Year - 10;
            if (!selected)
                return [];

            ObservedToken = ct;
            Entered.TrySetResult();
            await Probe.Task;
            try
            {
                // 实际来源还在使用生产 token；等待句柄由 CTS 拥有，不由来源关闭。
                HandleWasSignaled.TrySetResult(ct.WaitHandle.WaitOne(0));
            }
            catch (ObjectDisposedException ex)
            {
                HandleWasSignaled.TrySetException(ex);
            }
            await Finish.Task;
            return [];
        }

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Unexpected<Task<Anime?>>();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Studio>>>();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Tag>>>();
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
