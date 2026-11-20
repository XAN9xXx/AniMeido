using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Reflection;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopArchivePageTests
{
    [Fact]
    public Task ArchivesDeferReadsUntilMounted()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            await new ArchiveService(fixture.Factory).UpsertArchiveAsync(1, "合成档案", 8, "笔记");
            var page = fixture.Archives(new Source());
            await page.OnNavigatedToAsync(1);
            Assert.Empty(((ListView)page.FindName("ArchiveList")).Items);
        });

    [Fact]
    public Task ArchivesPreserveSelectionAndReloadRecordsAfterReturn()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = new ArchiveService(fixture.Factory);
            await archive.SaveArchiveWithTagsAsync(1, "作品1", 8, "原笔记", ["旅行"]);
            await archive.UpsertArchiveAsync(2, "作品2", null, "");
            var page = fixture.Archives(new Source());
            await page.OnNavigatedToAsync(1);
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                Assert.Equal("作品1", ((TextBlock)page.FindName("ArchiveTitle")).Text);
                Assert.Equal(Visibility.Collapsed, ((Button)page.FindName("AddManualWatchButton")).Visibility);
                await DetachAsync(root, page); await page.PendingRead;
                await archive.AddEntryAsync(1, DateTimeOffset.Now, 2, "再看一集");
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                Assert.Equal("作品1", ((TextBlock)page.FindName("ArchiveTitle")).Text);
                Assert.Single(((ListView)page.FindName("EntryList")).Items);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; }
        });

    [Fact]
    public Task OldMetadataCannotChangeTheNextArchiveSelection()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = new ArchiveService(fixture.Factory);
            await archive.UpsertArchiveAsync(1, "作品1", null, "");
            await archive.UpsertArchiveAsync(2, "作品2", null, "");
            var late = new TaskCompletionSource<AniMeido.Contracts.Models.Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = fixture.Archives(new Source { Detail = (id, _) => id == 1 ? late.Task : Task.FromResult<AniMeido.Contracts.Models.Anime?>(Item(id)) });
            await page.OnNavigatedToAsync(1);
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                var list = (ListView)page.FindName("ArchiveList");
                list.SelectedItem = list.Items.OfType<ArchiveListItem>().Single(a => a.Archive.AnimeId == 2);
                await page.PendingRead;
                late.SetResult(Item(1) with { AirDate = new DateOnly(1999, 1, 1) }); await Task.Delay(20);
                Assert.Equal("作品2", ((TextBlock)page.FindName("ArchiveTitle")).Text);
                Assert.DoesNotContain("1999", ((TextBlock)page.FindName("ArchiveAnimeMetadata")).Text);
            }
            finally { late.TrySetResult(null); await DetachAsync(root, page); await page.PendingRead; }
        });

    [Fact]
    public Task ConfirmedArchiveSaveFinishesBeforeReturningRead()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = new ArchiveService(fixture.Factory);
            await archive.UpsertArchiveAsync(1, "作品1", 6, "原笔记");
            var page = fixture.Archives(new Source());
            await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                _ = page.SaveArchiveAsync(1, "作品1", 9, "新笔记", ["旅行"]);
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page); await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            try
            {
                await page.PendingActivation; await page.PendingRead;
                var saved = await archive.GetArchiveAsync(1);
                Assert.Equal(9, saved!.PersonalRating); Assert.Equal("新笔记", saved.SummaryNote);
                Assert.Equal("旅行", Assert.Single(await archive.GetAnimeTagsAsync(1)));
                Assert.Equal("我的评分 9.0", ((TextBlock)page.FindName("ArchiveRatingText")).Text);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; await page.PendingOperation; }
        });

    [Fact]
    public Task EntryEditorKeepsInvalidInputOpenAndSavesAfterCorrection()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = new ArchiveService(fixture.Factory);
            await archive.UpsertArchiveAsync(1, "作品1", null, "");
            var page = fixture.Archives(new Source());
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                Invoke(Find<Button>(page, b => b.Content as string == "写感想"));
                var dialog = await DialogAsync(page);
                Invoke(Find<Button>(dialog, b => b.Name == "PrimaryButton"));
                await page.PendingOperation; await Task.Delay(20);
                Assert.True(dialog.IsLoaded);
                var input = Assert.IsType<StackPanel>(dialog.Content).Children.OfType<TextBox>().Single();
                input.Text = "旅途从这里开始";
                Invoke(Find<Button>(dialog, b => b.Name == "PrimaryButton"));
                await page.PendingOperation; await page.PendingRead;
                Assert.Equal("旅途从这里开始", Assert.Single(await archive.GetEntriesAsync(1)).Body);
                Assert.Single(((ListView)page.FindName("EntryList")).Items);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; await page.PendingOperation; }
        });

    [Fact]
    public Task ScreenshotDeepLinkSelectsItsPageAndMissingOriginalDisablesFileActions()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = new ArchiveService(fixture.Factory);
            var items = Enumerable.Range(0, 55).Select(i => Shot(fixture, i)).ToArray();
            await archive.ImportScreenshotsAsync(items);
            var page = fixture.Archives(new Source());
            await page.OnNavigatedToAsync("shot54");
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                Assert.Equal("2 / 2", ((TextBlock)page.FindName("ScreenshotPageText")).Text);
                Assert.Equal("shot54", Assert.IsType<AnimeScreenshot>(((GridView)page.FindName("ScreenshotList")).SelectedItem).ScreenshotId);
                Assert.False(((Button)page.FindName("ScreenshotOpenButton")).IsEnabled);
                Assert.False(((Button)page.FindName("ScreenshotExportButton")).IsEnabled);
                Assert.Equal("原图文件已缺失", ((TextBlock)page.FindName("ScreenshotDetailFileState")).Text);
                Invoke((Button)page.FindName("ScreenshotPreviousButton"));
                Assert.Equal("1 / 2", ((TextBlock)page.FindName("ScreenshotPageText")).Text);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; }
        });

    [Fact]
    public Task ScreenshotFiltersResetPagingAndBatchSelectionKeepsThePreview()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = new ArchiveService(fixture.Factory);
            await archive.ImportScreenshotsAsync(Enumerable.Range(0, 55).Select(i => Shot(fixture, i) with { AnimeId = i == 54 ? null : 1 }).ToArray());
            var page = fixture.Archives(new Source()); await page.OnNavigatedToAsync("shot54");
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                ((CheckBox)page.FindName("ScreenshotUnassignedOnly")).IsChecked = true; await Task.Delay(20);
                Assert.Equal("1 / 1", ((TextBlock)page.FindName("ScreenshotPageText")).Text);
                Assert.Equal("截图 1 张", ((TextBlock)page.FindName("ScreenshotCountText")).Text);
                Invoke((Button)page.FindName("ScreenshotBatchButton"));
                Assert.Equal(ListViewSelectionMode.Multiple, ((GridView)page.FindName("ScreenshotList")).SelectionMode);
                Assert.Equal(Visibility.Visible, ((Grid)page.FindName("ScreenshotDetailsPanel")).Visibility);
                Invoke((Button)page.FindName("ScreenshotBatchButton"));
                Assert.Equal(ListViewSelectionMode.Single, ((GridView)page.FindName("ScreenshotList")).SelectionMode);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; }
        });

    [Fact]
    public Task StatisticsAndReviewKeepPersonalCountsWithoutPlayer()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = new ArchiveService(fixture.Factory);
            await archive.SaveArchiveWithTagsAsync(1, "作品1", 8, "笔记", ["旅行"]);
            await archive.AddEntryAsync(1, DateTimeOffset.Now, 1, "开篇");
            var page = fixture.Archives(new Source());
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                Invoke((ButtonBase(page, "StatisticsSectionButton"))); await page.PendingRead;
                Assert.Equal("1 部", ((TextBlock)page.FindName("StatisticsArchiveCountText")).Text);
                Assert.Equal("100%", ((TextBlock)page.FindName("StatisticsCoveragePercentText")).Text);
                Assert.Equal(Visibility.Collapsed, ((Border)page.FindName("StatisticsWatchCard")).Visibility);
                Invoke((ButtonBase(page, "ReviewSectionButton"))); await page.PendingRead;
                Assert.Equal("1 条", ((TextBlock)page.FindName("ReviewEntryCount")).Text);
                Assert.Single(((ItemsControl)page.FindName("ReviewMoments")).Items);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; }
        });

    private static Microsoft.UI.Xaml.Controls.Primitives.ButtonBase ButtonBase(ArchivePage page, string name)
        => (Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)page.FindName(name);

    [Fact]
    public Task PendingKeepSurvivesLeavingAndReturnsInTheKeptGroup()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            using (var connection = await fixture.Factory.OpenAsync())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO public_id_issues(AnimeId,Kind,TitleSnapshot,DetectedAt) VALUES(1,'deleted','待确认作品','2026-01-01')";
                await command.ExecuteNonQueryAsync();
            }
            var page = fixture.Archives(new Source());
            await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
            Invoke((Button)page.FindName("PendingReviewBanner")); await Task.Delay(20); root.UpdateLayout();
            var keep = Find<Button>(page, b => b.Content as string == "暂不处理");
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                Invoke(keep); Invoke(keep);
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page); await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            try
            {
                await page.PendingActivation; await page.PendingRead;
                Assert.Empty(((ItemsControl)page.FindName("PendingReviewList")).Items);
                Assert.Single(((ItemsControl)page.FindName("KeptReviewList")).Items);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; await page.PendingOperation; }
        });

    [Fact]
    public Task LeavingAnArchiveEditorDiscardsUnconfirmedChanges()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = new ArchiveService(fixture.Factory);
            await archive.UpsertArchiveAsync(1, "作品1", 6, "原笔记");
            var page = fixture.Archives(new Source());
            await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
            Invoke(Find<Button>(page, b => b.Content as string == "编辑档案"));
            var dialog = await DialogAsync(page);
            Assert.IsType<StackPanel>(dialog.Content).Children.OfType<NumberBox>().Single().Value = 9;
            await DetachAsync(root, page); await Task.Delay(20);
            Assert.Equal(6, (await archive.GetArchiveAsync(1))!.PersonalRating);
            Assert.True(page.PendingOperation.IsCompleted);
        });

    [Fact]
    public Task ScreenshotSettingsFinishAnAcceptedSaveAfterLeaving()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.Archives(new Source());
            await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
            typeof(ArchivePage).GetMethod("OnScreenshotSettingsClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [page, new RoutedEventArgs()]);
            var dialog = await DialogAsync(page);
            var fields = Assert.IsType<StackPanel>(dialog.Content);
            fields.Children.OfType<ToggleSwitch>().First().IsOn = false;
            var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "screenshots");
            fields.Children.OfType<TextBox>().Single().Text = path;
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                Invoke(Find<Button>(dialog, b => b.Name == "PrimaryButton")); await Task.Delay(20);
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page); await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            try
            {
                await page.PendingActivation; await page.PendingRead; await page.PendingOperation;
                var settings = await new ArchiveService(fixture.Factory).GetScreenshotSettingsAsync();
                Assert.False(settings.Enabled); Assert.Equal(path, settings.RootDirectory);
                Assert.False(fixture.ScreenshotShortcut!.IsEnabled);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; }
        });

    private static void Invoke(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button)
        => typeof(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);

    private static AnimeScreenshot Shot(Fixture fixture, int id) => new($"shot{id}", Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, $"missing{id}.png"),
        "synthetic", DateTimeOffset.Now.AddMinutes(-id), "合成窗口", "test", 800, 450, null, null, null, null, "合成备注", false);

    private static async Task<ContentDialog> DialogAsync(ArchivePage page)
    {
        var field = typeof(ArchivePage).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (field.GetValue(page) is null && DateTime.UtcNow < deadline) await Task.Delay(10);
        var dialog = Assert.IsType<ContentDialog>(field.GetValue(page)); await Task.Delay(20); return dialog;
    }
}
