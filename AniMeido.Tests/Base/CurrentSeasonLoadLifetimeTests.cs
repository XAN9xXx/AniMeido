using AniMeido.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using System.Text.Json;

namespace AniMeido.Tests;

[Collection(CurrentSeasonSessionCollection.Name)]
public sealed partial class CurrentSeasonLoadLifetimeTests : DbTestBase
{
    private readonly PersonalFeatureStateStore _featureState;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public CurrentSeasonLoadLifetimeTests()
    {
        _featureState = new PersonalFeatureStateStore(DbFactory);
        CurrentSeasonViewModel.ResetTimeMachineSession();
    }

    public enum LoadFailure
    {
        Http,
        Metadata,
        Json,
        InvalidOperation,
        Timeout,
    }

    [Theory]
    [InlineData(LoadFailure.Http, false)]
    [InlineData(LoadFailure.Http, true)]
    [InlineData(LoadFailure.Metadata, false)]
    [InlineData(LoadFailure.Metadata, true)]
    [InlineData(LoadFailure.Json, false)]
    [InlineData(LoadFailure.Json, true)]
    [InlineData(LoadFailure.InvalidOperation, false)]
    [InlineData(LoadFailure.InvalidOperation, true)]
    [InlineData(LoadFailure.Timeout, false)]
    [InlineData(LoadFailure.Timeout, true)]
    public async Task MainLoad_ReportsOnlyCurrentFailuresAndIgnoresCancelledLateErrors(
        LoadFailure failureKind,
        bool cancelBeforeFailure)
    {
        await RunProductionMigrationAsync();
        var source = new PendingScheduleSource();
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory),
            _featureState,
            new CalendarTestTime(new DateOnly(2026, 9, 18)), NullLogger.Instance);
        var loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);

        try
        {
            await source.ScheduleEntered.Task.WaitAsync(Budget);
            Assert.True(vm.IsLoading);
            if (cancelBeforeFailure)
            {
                vm.LoadSeasonalAnimeCommand.Cancel();
                vm.CancelSupplementaryLoads();
                Assert.True(source.ObservedToken.IsCancellationRequested);
            }

            source.Schedule.SetException(CreateFailure(failureKind));
            await loading.WaitAsync(Budget);

            Assert.False(vm.IsLoading);
            Assert.False(vm.HasData);
            Assert.Empty(vm.VisibleEntries);
            Assert.Equal(!cancelBeforeFailure, vm.IsError);
            if (cancelBeforeFailure)
            {
                Assert.Null(vm.ErrorMessage);
            }
            else
            {
                var prefix = failureKind switch
                {
                    LoadFailure.Http => "网络请求失败：",
                    LoadFailure.Metadata => "数据源请求失败：",
                    LoadFailure.Timeout => "网络请求超时，请检查网络后重试",
                    _ => "数据解析失败：",
                };
                Assert.StartsWith(prefix, vm.ErrorMessage);
            }
        }
        finally
        {
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();
            source.Schedule.TrySetResult([]);
            await Task.WhenAll(loading, vm.PendingOthersLoad,
                vm.PendingTimeMachineLoad, vm.PendingTimeMachineShownSave).WaitAsync(Budget);
        }
    }

    private static Exception CreateFailure(LoadFailure failureKind)
        => failureKind switch
        {
            LoadFailure.Http => new HttpRequestException("fixture late HTTP failure"),
            LoadFailure.Metadata => new MetadataServiceException("fixture late metadata failure"),
            LoadFailure.Json => new JsonException("fixture late JSON failure"),
            LoadFailure.InvalidOperation => new InvalidOperationException("fixture late parsing failure"),
            LoadFailure.Timeout => new TaskCanceledException("fixture late timeout"),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
        };

    private sealed class PendingScheduleSource : IAnimeDataSource
    {
        public TaskCompletionSource ScheduleEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<List<Anime>> Schedule { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ObservedToken { get; private set; }
        public Func<int, Season, CancellationToken, Task<List<Anime>>>? SeasonRequest { get; init; }
        public Func<int, CancellationToken, Task<Anime?>>? DetailRequest { get; init; }
        public Func<int, CancellationToken, Task<List<Tag>>>? TagsRequest { get; init; }

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
        {
            ObservedToken = ct;
            ScheduleEntered.TrySetResult();
            return Schedule.Task;
        }

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => SeasonRequest?.Invoke(year, season, ct) ?? Task.FromResult(new List<Anime>());

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
            => DetailRequest?.Invoke(animeID, ct) ?? Unexpected<Task<Anime?>>();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Studio>>>();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => TagsRequest?.Invoke(animeID, ct) ?? Unexpected<Task<List<Tag>>>();
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
