using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Xml.Linq;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopVisualLayoutTests
{
    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10)]
    [InlineData(DesktopVisualStyle.Fluent11)]
    [InlineData(DesktopVisualStyle.Paper)]
    public Task PosterKeepsItsCoverSizeAndDoesNotReserveTwoTitleLines(DesktopVisualStyle visual)
        => DesktopInteractionRenderTests.WithThemeAsync(visual, true, async root =>
        {
            var card = new AnimeCard
            {
                Anime = new Anime(1, "短标题", null, [], null, null, "", 2026, 1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            root.Children.Add(card);
            await Task.Delay(30);
            root.UpdateLayout();
            var cover = (Grid)card.FindName("CoverHost");
            var caption = (TextBlock)card.FindName("TitleText");
            Assert.Equal(172, cover.ActualWidth, 0);
            Assert.Equal(258, cover.ActualHeight, 0);
            Assert.Equal(2, caption.MaxLines);
            Assert.Equal(0, caption.MinHeight);
            Assert.True(caption.ActualHeight < caption.LineHeight * 2);
            Assert.Equal(Visibility.Collapsed, ((Border)card.FindName("ScoreBadge")).Visibility);
        });

    [Fact]
    [Trait("Category", "Mechanism")]
    public void ArchiveAndTodayUseTheReviewedDesktopLayout()
    {
        var archive = Page("ArchivePage");
        Assert.Equal("268,*", (string?)Named(archive, "ArchivesPanel").Attribute("ColumnDefinitions"));
        Assert.Equal("12", (string?)Named(archive, "DesktopArchiveListPane").Attribute("Padding"));

        var today = Page("TodayPage");
        var broadcast = Template(today, "BroadcastEntryTemplate");
        Assert.Contains(broadcast.Descendants(), e => (string?)e.Attribute("Width") == "56" && (string?)e.Attribute("Height") == "84");
        var day = Template(today, "BroadcastDayTemplate");
        Assert.Contains(day.Descendants(), e => (string?)e.Attribute("MinHeight") == "68");
        Assert.DoesNotContain(day.Descendants().Attributes(), a => a.Name.LocalName.Contains("ToolTip", StringComparison.Ordinal));
        var plan = Template(today, "PlanRowCompactTemplate");
        Assert.Contains(plan.Descendants(), e => (string?)e.Attribute("ColumnDefinitions") == "48,*,Auto");
        var browse = Template(today, "BrowseEntryTemplate");
        Assert.Contains(browse.Descendants(), e => (string?)e.Attribute("Width") == "196");
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [Trait("Category", "Mechanism")]
    public Task DetailedCardKeepsFourReadableLinesAndDoesNotAllocateAMissingScore(double scale)
        => DesktopInteractionRenderTests.WithThemeAsync(DesktopVisualStyle.Paper, true, async root =>
        {
            var xamlRoot = root.XamlRoot!;
            var original = xamlRoot.RasterizationScale;
            SetScale(xamlRoot, scale);
            try
            {
                var longTitle = string.Concat(Enumerable.Repeat("很长的标题", 12));
                var summary = string.Concat(Enumerable.Repeat("这段简介提供足够的正文来检查四行显示。", 12));
                var card = new AnimeCard
                {
                    Anime = new Anime(1, longTitle, null, [], new DateOnly(2026, 1, 1), null, summary, 2026, 1),
                    DisplayMode = AnimeCardDisplayMode.Detailed, Width = 344, VerticalAlignment = VerticalAlignment.Top,
                };
                root.Children.Add(card);
                await Task.Delay(120);
                root.UpdateLayout();
                var cover = (Grid)card.FindName("CoverHost");
                Assert.InRange(cover.ActualWidth, 129, 131);
                Assert.InRange(cover.ActualHeight, 194, 196);
                var description = (TextBlock)card.FindName("DescriptionText");
                Assert.Equal(4, description.MaxLines);
                Assert.InRange(description.ActualHeight, 4 * description.LineHeight - 2, 4 * description.LineHeight + 2);
                Assert.InRange(card.ActualHeight, 195, 198);
                Assert.Equal(1, ((TextBlock)card.FindName("DetailedTitle")).MaxLines);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)card.FindName("DetailedScore")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Grid)card.FindName("QuickActions")).Visibility);
            }
            finally { SetScale(xamlRoot, original); }
        }, DesktopFontStyle.Serif);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LoadedArchiveRowsKeepTheirSelectionWhileTheSidebarAndPaletteChange(bool dark)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Fluent10", ThemeSetting = dark ? "Dark" : "Light" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var archive = new ArchiveService(fixture.Factory);
            var title = string.Concat(Enumerable.Repeat("很长的档案名称", 4));
            await archive.UpsertArchiveAsync(1, title, 8.5, "");
            await archive.UpsertArchiveAsync(2, "无评分作品", null, "");
            var page = fixture.Archives(new Source());
            page.Width = 1160;
            await page.OnNavigatedToAsync(1);
            await AttachAsync(root, page);
            try
            {
                await page.PendingActivation;
                await page.PendingRead;
                await Task.Delay(40);
                root.UpdateLayout();
                var sidebar = (Border)page.FindName("DesktopArchiveListPane");
                Assert.Equal(new Thickness(12), sidebar.Padding);
                Assert.Equal(268, sidebar.ActualWidth, 0);
                var list = (ListView)page.FindName("ArchiveList");
                var record = Assert.IsType<ArchiveListItem>(list.SelectedItem);
                var item = Assert.IsType<ListViewItem>(list.ContainerFromItem(record));
                var indicator = Child<Border>(item, "DesktopListIndicator");
                var rowTitle = Child<TextBlock>(item, "ArchiveRowTitle");
                var count = Child<TextBlock>(item, "ArchiveRowCount");
                var rating = Child<TextBlock>(item, "ArchiveRowRating");
                Assert.Equal(title, rowTitle.Text);
                Assert.Equal(2, rowTitle.MaxLines);
                Assert.Equal("8.5 分", rating.Text);
                Assert.Equal(new Thickness(10, 8, 10, 8), item.Padding);
                Assert.InRange(item.ActualHeight, 52, 76);
                Assert.Equal(Microsoft.UI.Colors.White, ((SolidColorBrush)rowTitle.Foreground).Color);
                Assert.Equal(Microsoft.UI.Colors.White, ((SolidColorBrush)count.Foreground).Color);
                foreach (var (visual, width, radius) in new[] { (DesktopVisualStyle.Fluent10, 4, 0), (DesktopVisualStyle.Fluent11, 3, 2), (DesktopVisualStyle.Paper, 0, 0) })
                {
                    await theme.SetVisualStyleAsync(visual);
                    root.UpdateLayout();
                    Assert.Equal(width, indicator.Width);
                    Assert.Equal(radius, indicator.CornerRadius.TopLeft);
                    Assert.Same(record, list.SelectedItem);
                }
                var unrated = list.Items.OfType<ArchiveListItem>().Single(entry => entry.Archive.AnimeId == 2);
                Assert.Equal(Visibility.Collapsed, Child<TextBlock>((ListViewItem)list.ContainerFromItem(unrated), "ArchiveRowRating").Visibility);
                page.Width = 860;
                await Task.Delay(40);
                root.UpdateLayout();
                Assert.Equal(1, Grid.GetRow((FrameworkElement)page.FindName("DesktopArchiveDetailPane")));
                Assert.True(sidebar.ActualWidth > 268);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; }
        });

    private static T Child<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T match && match.Name == name) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find<T>(VisualTreeHelper.GetChild(parent, i), name) is { } child) return child;
        throw new InvalidOperationException($"找不到 {name}。");
    }

    private static T? Find<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T match && match.Name == name) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find<T>(VisualTreeHelper.GetChild(parent, i), name) is { } child) return child;
        return null;
    }

    private static void SetScale(XamlRoot root, double value)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var tree = typeof(XamlRoot).GetProperty("VisualTree", flags)!.GetValue(root)!;
        var scale = tree.GetType().GetProperty("RootScale", flags)!.GetValue(tree)!;
        scale.GetType().GetMethod("SetTestOverride", flags)!.Invoke(scale, [(float)value]);
        typeof(XamlRoot).GetMethod("RaiseChangedEvent", flags)!.Invoke(root, null);
    }

    private static XElement Named(XDocument page, string name) => Assert.Single(page.Descendants(), e =>
        (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == name);

    private static XElement Template(XDocument page, string key) => Assert.Single(page.Descendants(), e =>
        (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == key);

    private static XDocument Page(string name)
    {
        var ancestor = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(ancestor.FullName, "AniMeido.slnx")))
            ancestor = ancestor.Parent ?? throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
        return XDocument.Load(Path.Combine(ancestor.FullName, "Plugins", "AniMeido.Plugin.Base.Desktop", "Views", name + ".xaml"));
    }
}
