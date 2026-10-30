using System.Text.Json;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class ManagementViewModelTests : DbTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false, "http")]
    [InlineData(false, "json")]
    [InlineData(true, "http")]
    [InlineData(true, "json")]
    public async Task LateTagFailure_DoesNotReplaceTheNewerSuccessfulView(bool reload, string failure)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResult = new TaskCompletionSource<(List<Anime> Results, int Total)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayOldTag = false;
        var oldToken = CancellationToken.None;
        var source = new ManagementSource
        {
            TagSearch = (tag, token) =>
            {
                if (tag == "A-old" && delayOldTag)
                {
                    oldToken = token;
                    entered.TrySetResult();
                    return oldResult.Task;
                }
                return Task.FromResult<(List<Anime>, int)>(([Item(tag == "B-new" ? 2 : 10)], 1));
            },
        };
        var vm = await CreateViewModelAsync(source);
        if (reload)
            await vm.ShowTagsAsync();
        delayOldTag = true;
        var oldRequest = reload ? vm.LoadAsync() : vm.ShowTagsAsync();
        try
        {
            await entered.Task.WaitAsync(Budget);
            await vm.SelectTagAsync("B-new").WaitAsync(Budget);
            Assert.True(oldToken.IsCancellationRequested);
            AssertCurrentTagView(vm);

            oldResult.SetException(Failure(failure, "old request failed"));
            await oldRequest.WaitAsync(Budget);

            AssertCurrentTagView(vm);
        }
        finally
        {
            oldResult.TrySetResult(([Item(999)], 1));
            await oldRequest.WaitAsync(Budget);
            vm.CancelPendingLoads();
        }
    }

    [Theory]
    [InlineData("http", "网络请求失败：current request failed")]
    [InlineData("json", "数据解析失败：current request failed")]
    public async Task CurrentTagFailure_StillReportsTheFailureAndAllowsTheNextSelectionToRecover(
        string failure, string expectedMessage)
    {
        var source = new ManagementSource
        {
            TagSearch = (tag, _) => tag == "A-old"
                ? Task.FromException<(List<Anime>, int)>(Failure(failure, "current request failed"))
                : Task.FromResult<(List<Anime>, int)>(([Item(2)], 1)),
        };
        var vm = await CreateViewModelAsync(source);
        try
        {
            await vm.ShowTagsAsync().WaitAsync(Budget);

            Assert.True(vm.IsError);
            Assert.Equal(expectedMessage, vm.ErrorMessage);
            Assert.False(vm.IsLoading);
            Assert.False(vm.IsTagLoading);
            Assert.Empty(vm.TagEntries);

            await vm.SelectTagAsync("B-new").WaitAsync(Budget);

            AssertCurrentTagView(vm);
        }
        finally
        {
            vm.CancelPendingLoads();
        }
    }

    private async Task<ManagementViewModel> CreateViewModelAsync(ManagementSource source, ILogger? logger = null)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
        var savedTags = new SavedTagService(DbFactory);
        await savedTags.SaveTagAsync("A-old");
        await savedTags.SaveTagAsync("B-new");
        var vm = new ManagementViewModel(
            tracking, source, savedTags,
            new LocalSearchService(tracking, source, NullLogger<LocalSearchService>.Instance),
            new ArchiveService(DbFactory), logger ?? NullLogger.Instance);
        await vm.LoadAsync().WaitAsync(Budget);
        await vm.SelectSectionAsync(vm.StatusSections.Single(
            section => section.Status == AnimeTrackingStatus.Watching)).WaitAsync(Budget);
        Assert.Equal(1, Assert.Single(vm.Entries).Anime.ID);
        Assert.False(vm.IsError);
        return vm;
    }

    private static void AssertCurrentTagView(ManagementViewModel vm)
    {
        Assert.Equal(MineView.Tags, vm.View);
        Assert.Equal("B-new", vm.SelectedTag);
        Assert.Equal("收藏的标签", vm.HeaderText);
        Assert.Equal(new[] { 2 }, vm.TagEntries.Select(entry => entry.Anime.ID));
        Assert.Equal("共 1 部", vm.TagResultSummary);
        Assert.False(vm.IsError);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsTagLoading);
    }

    private static Exception Failure(string kind, string message) => kind switch
    {
        "http" => new HttpRequestException(message),
        "json" => new JsonException(message),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static Anime Item(int id)
        => new(id, $"作品{id}", null, [], null, null, string.Empty, 2026, 1);

    private sealed class ManagementSource : IAnimeDataSource
    {
        internal Func<string, CancellationToken, Task<(List<Anime> Results, int Total)>> TagSearch { get; init; }
            = (_, _) => Task.FromResult<(List<Anime>, int)>(([], 0));

        internal Func<int, CancellationToken, Task<Anime?>> Detail { get; init; }
            = (id, _) => Task.FromResult<Anime?>(Item(id));

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
            => Detail(animeID, ct);

        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(
            string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null)
            => TagSearch(tag, ct);

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(
            string keyword, int offset, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
