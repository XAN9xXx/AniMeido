using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

/// <summary>放送日历在拖放结束或返回页面时重新读取本地标记。</summary>
[Collection(CurrentSeasonSessionCollection.Name)]
public sealed class CurrentSeasonStatusReloadTests : DbTestBase
{
    public CurrentSeasonStatusReloadTests() => CurrentSeasonViewModel.ResetTimeMachineSession();

    [Fact]
    public async Task ReloadStatuses_RestoresAnimeAfterUnblocking()
    {
        // 屏蔽的作品不显示；解除屏蔽后应从原始日程中重新出现，而不是永久丢失。
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(2, AnimeTrackingStatus.Blocked);
        var vm = await LoadAsync(tracking);
        Assert.Equal("1", vm.TotalCountText);

        await tracking.RemoveStatusAsync(2);
        await vm.ReloadStatusesAsync();

        Assert.Equal("2", vm.TotalCountText);
    }

    [Fact]
    public async Task ReloadStatuses_KeepsExistingStatusesWhenReadFails()
    {
        // 读取失败时保留屏幕上的标记，不能全部显示成“未设置”。
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
        var vm = await LoadAsync(tracking);
        Assert.Equal("1", vm.MineCountText);

        SqliteConnection.ClearAllPools();
        CorruptDatabase();
        await vm.ReloadStatusesAsync();

        Assert.Equal("1", vm.MineCountText);
        Assert.Contains(
            vm.VisibleEntries,
            entry => entry.Anime.ID == 1 && entry.Status == AnimeTrackingStatus.Watching);
    }

    [Fact]
    public async Task ApplyStatuses_KeepsWriteThatStartedBeforeTheRead()
    {
        // 写入先开始、回读随后开始并读到写入前的旧值；写入完成后旧值不能把它盖掉。
        await RunProductionMigrationAsync();
        var vm = await LoadAsync(new TrackingService(DbFactory));
        var entry = vm.VisibleEntries.Single(item => item.Anime.ID == 1);

        entry.IsSavingStatus = true;
        var snapshot = vm.BeginStatusRead();
        entry.Status = AnimeTrackingStatus.Watching;
        entry.IsSavingStatus = false;
        vm.ApplyStatuses(new Dictionary<int, AnimeTrackingStatus>(), snapshot);

        Assert.Equal(AnimeTrackingStatus.Watching, entry.Status);

        // 之后开始的读取照常生效，不会一直拒绝外部的变化。
        vm.ApplyStatuses(
            new Dictionary<int, AnimeTrackingStatus> { [1] = AnimeTrackingStatus.Following },
            vm.BeginStatusRead());

        Assert.Equal(AnimeTrackingStatus.Following, entry.Status);
    }

    [Fact]
    public async Task ApplyStatuses_KeepsWriteThatStartedDuringTheRead()
    {
        await RunProductionMigrationAsync();
        var vm = await LoadAsync(new TrackingService(DbFactory));

        var snapshot = vm.BeginStatusRead();
        await vm.ToggleStatusAsync(1, AnimeTrackingStatus.Watching);
        vm.ApplyStatuses(new Dictionary<int, AnimeTrackingStatus>(), snapshot);

        Assert.Equal(
            AnimeTrackingStatus.Watching,
            vm.VisibleEntries.Single(item => item.Anime.ID == 1).Status);
    }

    [Fact]
    public async Task ApplyStatuses_DoesNotRemoveSavingItemForStaleBlockedRead()
    {
        await RunProductionMigrationAsync();
        var vm = await LoadAsync(new TrackingService(DbFactory));
        var entry = vm.VisibleEntries.Single(item => item.Anime.ID == 1);

        entry.IsSavingStatus = true;
        var snapshot = vm.BeginStatusRead();
        entry.Status = AnimeTrackingStatus.Watching;
        entry.IsSavingStatus = false;
        vm.ApplyStatuses(
            new Dictionary<int, AnimeTrackingStatus> { [1] = AnimeTrackingStatus.Blocked },
            snapshot);

        Assert.Contains(vm.VisibleEntries, item => ReferenceEquals(item, entry));
        Assert.Equal(AnimeTrackingStatus.Watching, entry.Status);
    }

    private static async Task<CurrentSeasonViewModel> LoadAsync(TrackingService tracking)
    {
        var vm = new CurrentSeasonViewModel(new ScheduleSource(), tracking);
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Assert.True(vm.HasData);
        return vm;
    }

    private sealed class ScheduleSource : IAnimeDataSource
    {
        // 两部作品都排在今天，按天视图的列表里就能看到它们。
        private static readonly int Today =
            AnimeListPresentation.ToBangumiWeekday(DateTime.Today.DayOfWeek);

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => Task.FromResult(new List<Anime> { Item(1), Item(2) });

        // “其他”一格的按季查询：本测试只关心周更作品，返回空。
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => Task.FromResult(new List<Anime>());
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Unexpected<Task<Anime?>>();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Studio>>>();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Tag>>>();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<VoiceActor>>>();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<CharacterRole>>>();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Unexpected<Task<List<PersonWork>>>();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null) => Unexpected<Task<(List<Anime>, int)>>();
        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Unexpected<Task<(List<Anime>, int)>>();

        private static Anime Item(int id) => new(
            id,
            $"作品{id}",
            null,
            [],
            null,
            null,
            string.Empty,
            2026,
            7,
            Today,
            7.0,
            MediaFormat: AnimeMediaFormat.Television);
    }
}
