using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class AnimeDetailRequestOwnershipTests : DbTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("network", false)]
    [InlineData("metadata", false)]
    [InlineData("timeout", false)]
    [InlineData("state", false)]
    [InlineData("json", false)]
    [InlineData("network", true)]
    [InlineData("metadata", true)]
    [InlineData("timeout", true)]
    [InlineData("state", true)]
    [InlineData("json", true)]
    public async Task OldFailureDoesNotChangeTheNewSelectionsErrorOrLoadingState(
        string failure,
        bool currentStillLoading)
    {
        await CreateBaseTablesAsync();
        var source = new DetailSource();
        var model = new AnimeDetailViewModel(source, new TrackingService(DbFactory), null!, NullLogger.Instance);
        Task first = Task.CompletedTask;
        Task current = Task.CompletedTask;

        try
        {
            first = model.LoadDetailCommand.ExecuteAsync(1);
            await source.FirstStarted.Task.WaitAsync(Budget);
            current = model.LoadDetailCommand.ExecuteAsync(-2);
            await source.CurrentStarted.Task.WaitAsync(Budget);
            if (!currentStillLoading)
            {
                source.Current.TrySetResult(CurrentAnime());
                await current.WaitAsync(Budget);
                Assert.Equal(-2, model.AnimeDetail?.ID);
            }

            source.First.TrySetException(Failure(failure));
            await first.WaitAsync(Budget);

            Assert.False(model.IsError);
            Assert.Null(model.ErrorMessage);
            Assert.Equal(currentStillLoading, model.IsLoading);
            source.Current.TrySetResult(CurrentAnime());
            await current.WaitAsync(Budget);
            Assert.Equal(-2, model.AnimeDetail?.ID);
            Assert.True(model.HasData);
            Assert.False(model.IsLoading);
        }
        finally
        {
            source.Release();
            await Task.WhenAll(first, current).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData("network", "网络请求失败：synthetic network")]
    [InlineData("metadata", "数据源请求失败：synthetic metadata")]
    [InlineData("timeout", "网络请求超时，请检查网络后重试")]
    [InlineData("state", "数据解析失败：synthetic state")]
    [InlineData("json", "数据解析失败：synthetic json")]
    public async Task CurrentFailureStillReportsTheExistingError(string failure, string message)
    {
        var source = new DetailSource();
        var model = new AnimeDetailViewModel(source, new TrackingService(DbFactory), null!, NullLogger.Instance);
        var loading = model.LoadDetailCommand.ExecuteAsync(1);

        try
        {
            await source.FirstStarted.Task.WaitAsync(Budget);
            source.First.TrySetException(Failure(failure));
            await loading.WaitAsync(Budget);

            Assert.True(model.IsError);
            Assert.Equal(message, model.ErrorMessage);
            Assert.False(model.HasData);
            Assert.False(model.IsLoading);
        }
        finally
        {
            source.Release();
            await loading.WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCancellationIsSilentForBothCancellationExceptionTypes(bool taskCancellation)
    {
        var source = new DetailSource();
        var model = new AnimeDetailViewModel(source, new TrackingService(DbFactory), null!, NullLogger.Instance);
        var loading = model.LoadDetailCommand.ExecuteAsync(1);

        try
        {
            await source.FirstStarted.Task.WaitAsync(Budget);
            model.LoadDetailCommand.Cancel();
            Assert.True(source.FirstToken.IsCancellationRequested);
            source.First.TrySetException(taskCancellation
                ? new TaskCanceledException("synthetic cancellation", null, source.FirstToken)
                : new OperationCanceledException(source.FirstToken));
            var error = await Record.ExceptionAsync(() => loading.WaitAsync(Budget));

            Assert.Null(error);
            Assert.False(model.IsError);
            Assert.Null(model.ErrorMessage);
            Assert.False(model.IsLoading);
        }
        finally
        {
            source.Release();
            await ObserveCompletionAsync(loading).WaitAsync(Budget);
        }
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        // 测试已经断言预期异常；外层仍限制真实任务的结束等待。
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static Exception Failure(string kind) => kind switch
    {
        "network" => new HttpRequestException("synthetic network"),
        "metadata" => new MetadataServiceException("synthetic metadata"),
        "timeout" => new TaskCanceledException("synthetic timeout"),
        "state" => new InvalidOperationException("synthetic state"),
        "json" => new JsonException("synthetic json"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static Anime CurrentAnime()
        // 本地作品不请求远端评分，因此这组请求归属测试不需要评分源。
        => new(-2, "当前本地作品", null, [], null, null, "本地档案", 0, 0);

    private sealed class DetailSource : IAnimeDataSource
    {
        public TaskCompletionSource<Anime?> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Anime?> Current { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CurrentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstToken { get; private set; }

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
        {
            if (animeID == 1)
            {
                FirstToken = ct;
                FirstStarted.TrySetResult();
                return First.Task;
            }

            Assert.Equal(-2, animeID);
            CurrentStarted.TrySetResult();
            return Current.Task;
        }

        public void Release()
        {
            First.TrySetResult(null);
            Current.TrySetResult(null);
        }

        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<Studio>());
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<CharacterRole>());
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => throw new NotSupportedException();
    }
}
