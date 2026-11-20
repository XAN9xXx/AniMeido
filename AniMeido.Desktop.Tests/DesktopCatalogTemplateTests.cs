using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopCatalogTemplateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NestedRowTemplatesPassEachAnimeToItsOwnCompleteCard(bool detailed)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var source = new SeasonSource((_, _, _) => Task.FromResult(new List<Anime>()));
            var page = new PastSeasonPage(source, null!, null!, new TrackingService(null!), null!, NullLogger<PastSeasonPage>.Instance);
            var first = new PastSeasonEntry(Item(1, "第一部"), AnimeTrackingStatus.Watching);
            var second = new PastSeasonEntry(Item(2, "第二部"), AnimeTrackingStatus.Following);
            object row = detailed ? new DesktopCatalogRow(first, second, true) : new DesktopCatalogPosterRow([first, second]);
            var presenter = new ContentPresenter
            {
                Content = row,
                DataContext = row,
                ContentTemplate = (DataTemplate)page.Resources[detailed ? "DesktopDetailedRowTemplate" : "DesktopPosterRowTemplate"],
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            root.Children.Add(presenter);
            await Task.Delay(50);
            root.UpdateLayout();
            var cards = Children<AnimeCard>(presenter).ToArray();
            Assert.Equal(2, cards.Length);
            Assert.Equal(new[] { 1, 2 }, cards.Select(card => card.Anime!.ID));
            Assert.Equal(new[] { AnimeTrackingStatus.Watching, AnimeTrackingStatus.Following }, cards.Select(card => card.TrackingStatus));
            foreach (var card in cards)
            {
                var title = (TextBlock)card.FindName(detailed ? "DetailedTitle" : "TitleText");
                Assert.Equal(card.Anime!.Title, title.Text);
                Assert.True(title.ActualWidth > 0);
                Assert.True(title.ActualHeight > 0);
                Assert.False(card.ShowQuickActions);
                if (detailed)
                    Assert.Equal(card.Anime.Description, ((TextBlock)card.FindName("DescriptionText")).Text);
            }
        });

    [Fact]
    public async Task LateCanceledSeasonDoesNotReplaceTheNewSeasonOrEndItsLoadingState()
    {
        var oldReply = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newReply = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new PastSeasonViewModel(new SeasonSource((year, _, _) => year == 2025 ? oldReply.Task : newReply.Task),
            new TrackingService(null!), NullLogger.Instance);
        using var cancellation = new CancellationTokenSource();
        var old = vm.LoadPastSeasonAnimeAsync(2025, Season.Spring, cancellation.Token);
        cancellation.Cancel();
        vm.Suspend();
        var current = vm.LoadPastSeasonAnimeAsync(2026, Season.Summer);
        oldReply.SetResult([Item(1, "旧季度")]);
        await old.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsLoading);
        Assert.Empty(vm.Entries);
        newReply.SetResult([Item(2, "新季度")]);
        await current.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(vm.IsLoading);
        Assert.Equal(new PastSeasonTarget(2026, Season.Summer), vm.Season);
        Assert.Equal(2, Assert.Single(vm.Entries).Anime.ID);
    }

    [Fact]
    public async Task SuspendingASuccessfulSeasonKeepsItsBrowsingConditionsAndRows()
    {
        var vm = new PastSeasonViewModel(new SeasonSource((_, _, _) => Task.FromResult(new List<Anime> { Item(1, "作品一"), Item(2, "作品二") })),
            new TrackingService(null!), NullLogger.Instance);
        await vm.LoadPastSeasonAnimeAsync(2026, Season.Summer);
        vm.SetQuery("作品");
        vm.SelectFormat(AnimeMediaFormat.Television);
        vm.SetSortKey(PastSeasonSortKey.Title);
        vm.ToggleSortDirection();
        vm.SetHideCompleted(true);
        var entries = vm.Entries;
        vm.Suspend();
        Assert.Same(entries, vm.Entries);
        Assert.Equal("作品", vm.Query);
        Assert.Equal(AnimeMediaFormat.Television, vm.SelectedFormat);
        Assert.Equal(PastSeasonSortKey.Title, vm.SortKey);
        Assert.False(vm.SortAscending);
        Assert.True(vm.HideCompleted);
    }

    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); ++index)
            foreach (var child in Children<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static Anime Item(int id, string title) => new(id, title, null, [], new DateOnly(2026, 7, 1), null,
        "实际模板使用的合成简介。", 2026, 7, Score: 8.7, MediaFormat: AnimeMediaFormat.Television);

    private sealed class SeasonSource(Func<int, Season, CancellationToken, Task<List<Anime>>> load) : IAnimeDataSource
    {
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => load(year, season, ct);
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null) => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => throw new NotSupportedException();
    }
}
