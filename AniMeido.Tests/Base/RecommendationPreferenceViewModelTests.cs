using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public class RecommendationPreferenceViewModelTests : DbTestBase
{
    [Fact]
    public async Task TagTimeout_RetryStartsANewRequest()
    {
        await RunProductionMigrationAsync();
        var attempts = 0;
        var source = new NoNetworkSource
        {
            Tags = () => ++attempts == 1
                ? Task.FromException<List<Tag>>(new TaskCanceledException("Request timed out"))
                : Task.FromResult(new List<Tag>()),
        };
        using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        using var service = CreateService(candidates);
        var vm = new RecommendationViewModel(service, CreateState());
        await vm.LoadAsync();

        await vm.LoadSelectedTagsAsync();
        Assert.NotNull(vm.TagError);
        Assert.False(vm.IsLoadingTags);
        await vm.LoadSelectedTagsAsync();

        Assert.Equal(2, attempts);
        Assert.Null(vm.TagError);
        Assert.False(vm.IsLoadingTags);
    }

    [Fact]
    public async Task FreshRound_IsReusedWithoutRegenerating()
    {
        var source = new NoNetworkSource { Popular = () => Task.FromResult(new List<Anime>()) };
        var state = await LoadRestoredRoundAsync(source, TimeSpan.Zero);

        // StartRound clears the round-local exclusions, so a retained skip proves
        // the restored round was reused rather than regenerated.
        Assert.Contains(1, state.SkippedIds);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task StaleRound_IsRegenerated()
    {
        var source = new NoNetworkSource { Popular = () => Task.FromResult(new List<Anime>()) };
        var state = await LoadRestoredRoundAsync(source, TimeSpan.FromHours(25));

        Assert.Empty(state.SkippedIds);
    }

    [Fact]
    public async Task StaleRound_SurvivesAFailedRegeneration()
    {
        var source = new NoNetworkSource { Popular = () => throw new HttpRequestException("offline") };
        var state = await LoadRestoredRoundAsync(source, TimeSpan.FromHours(25), vm =>
        {
            // The restored round must stay on screen when regeneration fails.
            Assert.True(vm.HasError);
            Assert.Equal(2, Assert.Single(vm.Items).Anime.ID);
        });

        Assert.Contains(1, state.SkippedIds);
    }

    private async Task<RecommendationBrowseState> LoadRestoredRoundAsync(
        NoNetworkSource source, TimeSpan age, Action<RecommendationViewModel>? assert = null)
    {
        await RunProductionMigrationAsync();
        using var candidates = new RecommendationCandidateProvider(
            source, NullLogger<RecommendationCandidateProvider>.Instance);
        using var service = CreateService(candidates);
        var state = CreateState(age);
        state.SkippedIds.Add(1);
        var vm = new RecommendationViewModel(service, state);

        await vm.LoadAsync();

        assert?.Invoke(vm);
        return state;
    }

    [Fact]
    public async Task FollowingClick_TogglesAndKeepsCurrentRecommendation()
    {
        await RunProductionMigrationAsync();
        using var candidates = new RecommendationCandidateProvider(new NoNetworkSource(), NullLogger<RecommendationCandidateProvider>.Instance);
        using var service = CreateService(candidates);
        var vm = new RecommendationViewModel(service, CreateState());
        await vm.LoadAsync();
        var item = vm.Items[0];

        await vm.ToggleFollowingAsync(item, CancellationToken.None);
        Assert.Equal("已关注，当前列表保留。", vm.Message);
        await vm.ToggleFollowingAsync(item, CancellationToken.None);

        Assert.Equal("已取消关注，当前列表保留。", vm.Message);
        Assert.Equal("+ 关注", vm.FollowLabel);
        Assert.False(vm.BrowseState.TrackingLabels.ContainsKey(item.Anime.ID));
        Assert.Empty(vm.SavingFollowingIds);
        Assert.Null(await new TrackingService(DbFactory).GetStatusAsync(item.Anime.ID));
        Assert.Contains(item, vm.Items);
    }

    [Fact]
    public async Task SavePreference_PersistsWithoutReplacingListOrSelection()
    {
        await RunProductionMigrationAsync();
        var source = new NoNetworkSource();
        using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        using var service = CreateService(candidates);
        var state = CreateState();
        var vm = new RecommendationViewModel(service, state);
        await vm.LoadAsync();
        var items = vm.Items;
        var selected = vm.SelectedItem;
        var feature = new RecommendationFeature(RecommendationFeatureKind.Tag, "旅行", "旅行");

        await vm.SetPreferenceAsync(feature, RecommendationAdjustment.Like);

        Assert.Same(items, vm.Items);
        Assert.Same(selected, vm.SelectedItem);
        Assert.True(state.HasPendingPreferences);
        Assert.Equal(0, source.Calls);
        Assert.Equal(RecommendationAdjustment.Like, Assert.Single(await service.GetFeaturePreferencesAsync()).Adjustment);

        await vm.SetPreferenceAsync(feature, null);
        Assert.Empty(await service.GetFeaturePreferencesAsync());
        Assert.Same(items, vm.Items);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task SaveTag_FailureRetainsOriginalStateAndReenablesEditor()
    {
        await RunProductionMigrationAsync();
        using var candidates = new RecommendationCandidateProvider(new NoNetworkSource(), NullLogger<RecommendationCandidateProvider>.Instance);
        using var service = CreateService(candidates);
        var vm = new RecommendationViewModel(service, CreateState());
        var tag = new RecommendationTagPreference(
            new RecommendationFeature(RecommendationFeatureKind.Tag, "旅行", "旅行"), null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            vm.SaveTagAsync(tag, RecommendationAdjustment.Like, cancellation.Token));

        Assert.Null(tag.Adjustment);
        Assert.True(tag.CanEdit);
        Assert.False(vm.BrowseState.HasPendingPreferences);
    }

    [Fact]
    public async Task ReturnVisit_RestoresSelectionSectionAndTemporarySkipWithoutNetwork()
    {
        await RunProductionMigrationAsync();
        var source = new NoNetworkSource();
        using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        using var service = CreateService(candidates);
        var state = CreateState();
        state.SelectedAnimeId = 2;
        state.SkippedIds.Add(1);
        state.Section = "profile";
        state.VerticalOffset = 120;
        var vm = new RecommendationViewModel(service, state);

        await vm.LoadAsync();

        Assert.Equal(2, vm.SelectedItem?.Anime.ID);
        Assert.Single(vm.Items);
        Assert.Equal("profile", state.Section);
        Assert.Equal(120, state.VerticalOffset);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Suspend_ClearsInterruptedLoadStateAndAsksForReloadOnce()
    {
        await RunProductionMigrationAsync();
        using var candidates = new RecommendationCandidateProvider(new NoNetworkSource(), NullLogger<RecommendationCandidateProvider>.Instance);
        using var service = CreateService(candidates);
        var state = CreateState();
        var savedProfile = state.Profile;
        var vm = new RecommendationViewModel(service, state)
        {
            // 模拟离开页面时整页刷新还没结束。
            IsBusy = true,
            IsRefreshing = true,
        };

        vm.Suspend();

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsRefreshing);
        Assert.Same(savedProfile, state.Profile);
        Assert.True(vm.TakeResumeReload());
        Assert.False(vm.TakeResumeReload());
    }

    private RecommendationService CreateService(RecommendationCandidateProvider candidates) => new(
        DbFactory, new TrackingService(DbFactory), new SavedTagService(DbFactory),
        new ArchiveService(DbFactory), new BrowseHistoryService(DbFactory),
        new ActionCenterService(DbFactory), new CacheService(DbFactory), candidates);

    private static RecommendationBrowseState CreateState(TimeSpan? age = null) => new()
    {
        Snapshot = new RecommendationSnapshot(RecommendationSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero), false, [Item(1), Item(2)]),
    };

    private static RecommendationItem Item(int id) => new(
        new Anime(id, $"作品{id}", null, [], null, null, "", 2026, 1), 1, [], false, true);

    // These scenarios must never refresh candidates or access a remote service.
    private sealed class NoNetworkSource : IAnimeDataSource
    {
        public int Calls { get; private set; }
        public Func<Task<List<Tag>>>? Tags { get; init; }
        public Func<Task<List<Anime>>>? Popular { get; init; }
        private T Unexpected<T>() { Calls++; throw new NotSupportedException("Unexpected data-source request."); }
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => Popular is { } popular ? popular() : Unexpected<Task<List<Anime>>>();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => Unexpected<Task<List<Anime>>>();
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Unexpected<Task<Anime?>>();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Studio>>>();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => Tags is { } tags ? tags() : Unexpected<Task<List<Tag>>>();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<VoiceActor>>>();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<CharacterRole>>>();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Unexpected<Task<List<PersonWork>>>();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null) => Unexpected<Task<(List<Anime>, int)>>();
        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Unexpected<Task<(List<Anime>, int)>>();
    }
}
