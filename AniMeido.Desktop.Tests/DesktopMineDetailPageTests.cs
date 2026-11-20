using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Data.Sqlite;
using System.Reflection;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopMineDetailPageTests
{
    [Theory]
    [InlineData(AniMeido.App.Services.DesktopVisualStyle.Fluent10)]
    [InlineData(AniMeido.App.Services.DesktopVisualStyle.Fluent11)]
    [InlineData(AniMeido.App.Services.DesktopVisualStyle.Paper)]
    public Task TagFavoritesUseStateFeedbackWithoutAContextPopupAndKeepKeyboardAccess(AniMeido.App.Services.DesktopVisualStyle visual)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new AniMeido.App.Services.DesktopSettingsStore(fixture);
            await store.SaveAsync(new AniMeido.App.Services.DesktopSettings { VisualStyle = visual.ToString(), ThemeSetting = "Dark" });
            await using var theme = new AniMeido.App.Services.ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var page = fixture.Details(new Source { Tags = (_, _) => Task.FromResult(new List<Tag> { new("旅行") }) });
            await page.OnNavigatedToAsync(1);
            try
            {
                await AttachAsync(root, page); await page.PendingActivation;
                var tags = (Panel)page.FindName("TagContainer");
                var button = Find<Button>(tags, item => item.Tag as string == "旅行");
                Assert.Null(button.ContextFlyout);
                Assert.True(button.IsTabStop);
                Assert.Equal(Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden, button.KeyboardAcceleratorPlacementMode);
                Assert.Equal(2, button.KeyboardAccelerators.Count);
                Assert.All(button.KeyboardAccelerators, shortcut => Assert.Same(button, shortcut.ScopeOwner));
                root.UpdateLayout(); await Task.Delay(20);
                Assert.True(button.Focus(FocusState.Keyboard));
                await page.ToggleSavedTagAsync("旅行");
                root.UpdateLayout(); await Task.Delay(20);
                Assert.Same(button, Find<Button>(tags, item => item.Tag as string == "旅行"));
                Assert.Equal(FocusState.Keyboard, button.FocusState);
                Assert.Equal("已收藏", Microsoft.UI.Xaml.Automation.AutomationProperties.GetItemStatus(button));
                Assert.Same(Application.Current.Resources["DesktopButtonStyle"], button.Style);
                Assert.Same(Application.Current.Resources["DesktopSelectedBrush"], button.Background);
                Assert.Same(Application.Current.Resources["DesktopAccentBrush"], button.BorderBrush);
                Invoke(button);
                Assert.Equal(typeof(TagSearchResultPage), fixture.Navigation.Page);
                Assert.Equal("旅行", fixture.Navigation.Parameter);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task DetailsDeferNavigationUntilAttached()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new Source();
            var page = fixture.Details(source);
            await page.OnNavigatedToAsync(1);
            Assert.Equal(0, source.DetailCalls);
            Assert.Null(page.ViewModel.AnimeDetail);
        });

    [Fact]
    public Task DetailsDiscardOldWorkAndTagsWhenNavigationChangesDuringARead()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var delayed = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new Source { Detail = (id, _) => id == 1 ? delayed.Task : Task.FromResult<Anime?>(Item(id)) };
            var page = fixture.Details(source);
            await page.OnNavigatedToAsync(1);
            try
            {
                await AttachAsync(root, page);
                var previous = page.PendingActivation;
                await page.OnNavigatedToAsync(2);
                delayed.SetResult(Item(1));
                await previous;
                Assert.Equal(2, page.ViewModel.AnimeDetail?.ID);
                Assert.Equal(Visibility.Collapsed, ((Button)page.FindName("OnlinePlayButton")).Visibility);
                Assert.False(page.ViewModel.IsLoading);
                Assert.True(page.ViewModel.HasRatingData);
                Assert.Equal(2, page.ViewModel.RatingRows.Count);
            }
            finally { delayed.TrySetResult(null); await DetachAsync(root, page); await page.PendingActivation; await page.PendingOperation; }
        });

    [Fact]
    public Task DetachedDetailReloadsCurrentWorkAfterTheOldRequestFinishes()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var delayed = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var source = new Source { Detail = (id, _) => ++calls == 1 ? delayed.Task : Task.FromResult<Anime?>(Item(id) with { Title = "返回后的资料" }) };
            var page = fixture.Details(source);
            await page.OnNavigatedToAsync(1);
            try
            {
                await AttachAsync(root, page);
                var previous = page.PendingActivation;
                await DetachAsync(root, page);
                await AttachAsync(root, page);
                await page.PendingActivation;
                delayed.SetResult(Item(1) with { Title = "离页前的旧资料" });
                await previous;
                Assert.Equal("返回后的资料", page.ViewModel.AnimeDetail?.Title);
                Assert.False(page.ViewModel.IsLoading);
            }
            finally { delayed.TrySetResult(null); await DetachAsync(root, page); await page.PendingActivation; await page.PendingOperation; }
        });

    [Fact]
    public Task MineKeepsSingleAndBatchChangesInOneQueueAndReturnsAfterBothCommit()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var tracking = new TrackingService(fixture.Factory);
            await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await tracking.SetStatusAsync(2, AnimeTrackingStatus.Watching);
            var page = fixture.Mine(new Source());
            await AttachAsync(root, page);
            await page.PendingActivation;
            var entries = page.ViewModel.Entries.ToArray();
            Assert.Equal(2, entries.Length);
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                _ = page.ChangeEntriesAsync([entries[0]], AnimeTrackingStatus.PlanToWatch);
                _ = page.ChangeEntriesAsync(entries, AnimeTrackingStatus.Completed);
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page);
                await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            try
            {
                await page.PendingOperation;
                await page.PendingActivation;
                Assert.Equal(AnimeTrackingStatus.Completed, await tracking.GetStatusAsync(1));
                Assert.Equal(AnimeTrackingStatus.Completed, await tracking.GetStatusAsync(2));
                Assert.Empty(page.ViewModel.Entries);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; }
        });

    [Fact]
    public Task MineBatchSelectionAndSavedTagNavigationUseTheirActualSurfaces()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            await new TrackingService(fixture.Factory).SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await new SavedTagService(fixture.Factory).SaveTagAsync("科幻");
            var page = fixture.Mine(new Source());
            try
            {
                await AttachAsync(root, page); await page.PendingActivation;
                Assert.Equal(Visibility.Visible, ((GridView)page.FindName("MineGrid")).Visibility);
                page.SetBatchMode(true);
                var list = (ListView)page.FindName("MineList");
                list.SelectedItems.Add(Assert.Single(page.ViewModel.Entries));
                Assert.Equal(ListViewSelectionMode.Multiple, list.SelectionMode);
                Assert.True(((Button)page.FindName("BatchRemoveButton")).IsEnabled);
                Invoke((Button)page.FindName("TagsNavButton"));
                await page.PendingRead;
                Assert.Equal(MineView.Tags, page.ViewModel.View);
                Assert.Equal(Visibility.Collapsed, ((Border)page.FindName("BatchBar")).Visibility);
                Assert.Empty(list.SelectedItems);
                Assert.Equal("科幻", Assert.Single(page.ViewModel.TagList).TagName);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    [Trait("Category", "Mechanism")]
    public Task PersonalArchiveCommitsRatingNoteAndTagsAfterConfirmedSaveAndLeave()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.Details(new Source());
            await page.OnNavigatedToAsync(1); await AttachAsync(root, page); await page.PendingActivation;
            var edit = (Button)page.FindName("PersonalArchiveButton");
            Invoke(edit);
            var dialog = await DialogAsync(page);
            var fields = Assert.IsType<StackPanel>(dialog.Content);
            fields.Children.OfType<NumberBox>().Single().Value = 8.5;
            fields.Children.OfType<TextBox>().First().Text = "科幻，旅行";
            fields.Children.OfType<TextBox>().Last().Text = "一起走过的故事";
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                Invoke(Find<Button>(dialog, button => button.Name == "PrimaryButton"));
                await Task.Delay(20);
                Assert.False(page.PendingOperation.IsCompleted);
                Assert.False(edit.IsEnabled);
                await DetachAsync(root, page);
                await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            try
            {
                await page.PendingOperation; await page.PendingActivation;
                var archive = new ArchiveService(fixture.Factory);
                var saved = await archive.GetArchiveAsync(1);
                Assert.Equal(8.5, saved!.PersonalRating);
                Assert.Equal("一起走过的故事", saved.SummaryNote);
                Assert.Equal(new[] { "旅行", "科幻" }.Order(), (await archive.GetAnimeTagsAsync(1)).Order());
                Assert.True(((Button)page.FindName("PersonalArchiveButton")).IsEnabled);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    [Trait("Category", "Mechanism")]
    public Task ATagFailureRollsBackTheWholePersonalArchiveEdit()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = new ArchiveService(fixture.Factory);
            await archive.SaveArchiveWithTagsAsync(1, "作品1", 6, "原笔记", ["原标签"]);
            var page = fixture.Details(new Source());
            await page.OnNavigatedToAsync(1); await AttachAsync(root, page); await page.PendingActivation;
            using (var connection = await fixture.Factory.OpenAsync())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TRIGGER reject_tag BEFORE INSERT ON anime_personal_tags BEGIN SELECT RAISE(ABORT, 'fixture tag failure'); END";
                await command.ExecuteNonQueryAsync();
            }
            try
            {
                Invoke((Button)page.FindName("PersonalArchiveButton"));
                var dialog = await DialogAsync(page);
                var fields = Assert.IsType<StackPanel>(dialog.Content);
                fields.Children.OfType<NumberBox>().Single().Value = 9;
                fields.Children.OfType<TextBox>().First().Text = "新标签";
                fields.Children.OfType<TextBox>().Last().Text = "新笔记";
                Invoke(Find<Button>(dialog, button => button.Name == "PrimaryButton"));
                await page.PendingOperation;
                var saved = await archive.GetArchiveAsync(1);
                Assert.Equal(6, saved!.PersonalRating);
                Assert.Equal("原笔记", saved.SummaryNote);
                Assert.Equal("原标签", Assert.Single(await archive.GetAnimeTagsAsync(1)));
                Assert.True(((InfoBar)page.FindName("ErrorInfoBar")).IsOpen);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task CharacterCardsRemainHorizontalAndUseTheSelectedActorsNavigation()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new Source { Characters = (_, _) => Task.FromResult(Enumerable.Range(1, 20)
                .Select(id => new CharacterRole(id, $"角色{id}", null, null, [new VoiceActor(100 + id, $"配音{id}", null)])).ToList()) };
            var page = fixture.Details(source);
            await page.OnNavigatedToAsync(1);
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; root.UpdateLayout();
                var cards = (ItemsRepeater)page.FindName("CharacterPanel");
                Assert.Equal(Orientation.Horizontal, Assert.IsType<StackLayout>(cards.Layout).Orientation);
                Invoke(Assert.IsType<Button>(cards.TryGetElement(0)));
                Assert.Equal(typeof(PersonSearchResultPage), fixture.Navigation.Page);
                Assert.Equal((101, "配音1"), fixture.Navigation.Parameter);
                Assert.Equal(Visibility.Visible, ((StackPanel)page.FindName("CharacterSection")).Visibility);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task SavingASavedTagSurvivesLeaveAndRepeatedInputKeepsOneMutation()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.Details(new Source { Tags = (_, _) => Task.FromResult(new List<Tag> { new("旅行") }) });
            await page.OnNavigatedToAsync(1); await AttachAsync(root, page); await page.PendingActivation;
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                _ = page.ToggleSavedTagAsync("旅行");
                await page.ToggleSavedTagAsync("旅行");
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page); await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            try
            {
                await page.PendingActivation;
                Assert.True(await new SavedTagService(fixture.Factory).IsTagSavedAsync("旅行"));
                Assert.Single(((StackPanel)page.FindName("TagContainer")).Children);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task ATrackingSaveCannotChangeTheNextWorksStatus()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.Details(new Source());
            await page.OnNavigatedToAsync(1); await AttachAsync(root, page); await page.PendingActivation;
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                _ = page.ChangeTrackingAsync(AnimeTrackingStatus.Completed);
                _ = page.OnNavigatedToAsync(2);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            try
            {
                await page.PendingActivation;
                Assert.Equal(AnimeTrackingStatus.Completed, await new TrackingService(fixture.Factory).GetStatusAsync(1));
                Assert.Equal(2, page.ViewModel.AnimeDetail?.ID);
                Assert.Equal(AnimeTrackingStatus.None, page.ViewModel.CurrentStatus);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task MineRejectsLateDetailsFromItsPreviousVisit()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            await new TrackingService(fixture.Factory).SetStatusAsync(1, AnimeTrackingStatus.Watching);
            var delayed = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var source = new Source { Detail = (id, _) => ++calls == 1 ? delayed.Task : Task.FromResult<Anime?>(Item(id) with { Title = "当前作品" }) };
            var page = fixture.Mine(source);
            try
            {
                await AttachAsync(root, page);
                while (source.DetailCalls == 0) await Task.Delay(10);
                var previous = page.PendingActivation;
                await DetachAsync(root, page); await AttachAsync(root, page); await page.PendingActivation;
                delayed.SetResult(Item(1) with { Title = "旧作品" });
                await previous;
                Assert.Equal("当前作品", Assert.Single(page.ViewModel.Entries).Anime.Title);
                Assert.False(page.ViewModel.IsLoading);
            }
            finally { delayed.TrySetResult(null); await DetachAsync(root, page); await page.PendingActivation; }
        });

    private static async Task<ContentDialog> DialogAsync(AnimeDetailPage page)
    {
        var field = typeof(AnimeDetailPage).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (field.GetValue(page) is null && DateTime.UtcNow < deadline) await Task.Delay(10);
        var dialog = Assert.IsType<ContentDialog>(field.GetValue(page));
        await Task.Delay(20);
        return dialog;
    }
}
