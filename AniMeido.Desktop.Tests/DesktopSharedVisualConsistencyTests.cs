using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Shape = Microsoft.UI.Xaml.Shapes.Shape;
using System.Reflection;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopSharedVisualConsistencyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PaperStatusNavigationKeepsNeutralRoundedSelectionWithoutAnUnderline(bool dark)
        => DesktopInteractionRenderTests.WithThemeAsync(DesktopVisualStyle.Paper, dark, async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.Mine(new Source());
            root.Children.Add(page);
            page.ApplyTemplate();
            root.UpdateLayout();
            await Task.Delay(30);
            await page.PendingActivation;
            root.UpdateLayout();
            for (var attempt = 0; attempt < 20 && !Children<Button>(page).Any(button => button.Tag is TrackingStatusSection); attempt++)
            { await Task.Delay(16); root.UpdateLayout(); }
            var buttons = Children<Button>(page).Where(button => button.Tag is TrackingStatusSection).ToArray();
            Assert.NotEmpty(buttons);
            var selected = buttons.First(button => ((TrackingStatusSection)button.Tag).IsSelected);
            var normal = buttons.First(button => !((TrackingStatusSection)button.Tag).IsSelected);
            Assert.Same(Application.Current.Resources["DesktopSubtleButtonTemplate"], normal.Template);
            var panel = Assert.IsType<Grid>(selected.Content);
            var selection = Assert.IsType<Border>(panel.Children[0]);
            var indicator = Assert.IsType<Border>(panel.Children[1]);
            Assert.Equal(Visibility.Visible, selection.Visibility);
            Assert.Equal(0, indicator.Height);
            Assert.True(selection.CornerRadius.TopLeft > 0);
            Assert.Same(Application.Current.Resources["DesktopSelectedBrush"], selection.Background);
            foreach (var state in new[] { "PointerOver", "Pressed", "Normal" })
            {
                Assert.True(VisualStateManager.GoToState(selected, state, false));
                Assert.Equal(Visibility.Visible, selection.Visibility);
                Assert.Equal(0, indicator.Height);
                Assert.Equal(0, ColorOf(normal.Background).A);
                Assert.True(selected.UseSystemFocusVisuals);
            }
            var tags = (Button)page.FindName("TagsNavButton");
            Invoke(tags);
            await page.PendingRead;
            var tagPanel = Assert.IsType<Grid>(tags.Content);
            Assert.Equal(0, ((Border)tagPanel.Children[1]).Height);
            Assert.Equal(Visibility.Visible, ((Border)tagPanel.Children[0]).Visibility);
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task PaperCheckBoxKeepsItsCheckedAndIndeterminatePaletteAcrossPointerStates(bool dark, bool indeterminate)
        => DesktopInteractionRenderTests.WithThemeAsync(DesktopVisualStyle.Paper, dark, async root =>
        {
            var checkbox = new CheckBox { Content = "未关联作品", IsThreeState = true, IsChecked = indeterminate ? null : true };
            root.Children.Add(checkbox);
            checkbox.ApplyTemplate();
            root.UpdateLayout();
            await Task.Delay(20);
            root.UpdateLayout();
            var shape = Children<FrameworkElement>(checkbox).Single(element => element.Name == "NormalRectangle");
            var prefix = indeterminate ? "Indeterminate" : "Checked";
            foreach (var (suffix, brush) in new[]
            {
                ("Normal", "DesktopAccentBrush"), ("PointerOver", "DesktopPrimaryButtonHoverBrush"),
                ("Pressed", "DesktopPrimaryButtonPressedBrush"), ("Normal", "DesktopAccentBrush"),
            })
            {
                Assert.True(VisualStateManager.GoToState(checkbox, prefix + suffix, false));
                Assert.Equal(ColorOf((Brush)Application.Current.Resources[brush]), ColorOf(Fill(shape)));
            }
            checkbox.IsEnabled = false;
            Assert.NotEqual(ColorOf((Brush)Application.Current.Resources["DesktopAccentBrush"]), ColorOf(Fill(shape)));
            checkbox.IsEnabled = true;
            Assert.Equal(ColorOf((Brush)Application.Current.Resources["DesktopAccentBrush"]), ColorOf(Fill(shape)));
        });

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public Task SharedSmallActionsKeepPaddingAndFitShortAndLongLabelsAtDesktopScale(double scale)
        => DesktopInteractionRenderTests.WithThemeAsync(DesktopVisualStyle.Paper, true, async root =>
        {
            Assert.True(Application.Current.Resources.TryGetValue("DesktopSmallActionButtonStyle", out var value));
            var style = Assert.IsType<Style>(value);
            var originalScale = root.XamlRoot!.RasterizationScale;
            SetScale(root.XamlRoot, scale);
            try
            {
                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                root.Children.Add(panel);
                foreach (var label in new[] { "清空", "换一批", "重新选择这一组里想看的作品" })
                {
                    var button = new Button { Content = label, Style = style, HorizontalAlignment = HorizontalAlignment.Left };
                    panel.Children.Add(button);
                    root.UpdateLayout();
                    await Task.Delay(20);
                    root.UpdateLayout();
                    Assert.True(double.IsNaN(button.Width));
                    Assert.Equal(new Thickness(10, 4, 10, 4), button.Padding);
                    Assert.Equal(30, button.MinHeight);
                    Assert.True(button.ActualHeight >= 30);
                    var text = Children<TextBlock>(button).Single(item => item.Text == label);
                    output.WriteLine($"scale={scale}; label={label}; button={button.ActualWidth}x{button.ActualHeight}; text={text.ActualWidth}x{text.ActualHeight}; padding={button.Padding}");
                    Assert.True(button.ActualWidth >= text.ActualWidth + 20);
                    Assert.True(Math.Round(button.ActualHeight, 2) >= Math.Round(text.ActualHeight + 8, 2));
                }
            }
            finally { SetScale(root.XamlRoot, originalScale); }
        }, DesktopFontStyle.Serif);

    [Theory]
    [InlineData(DesktopVisualStyle.Paper, false, 6)]
    [InlineData(DesktopVisualStyle.Paper, true, 6)]
    [InlineData(DesktopVisualStyle.Fluent10, false, 0)]
    [InlineData(DesktopVisualStyle.Fluent10, true, 0)]
    [InlineData(DesktopVisualStyle.Fluent11, false, 0)]
    [InlineData(DesktopVisualStyle.Fluent11, true, 0)]
    public Task SharedScoreBadgeUsesPaperCornersAndKeepsExistingFluentCorners(DesktopVisualStyle visual, bool dark, int radius)
        => DesktopInteractionRenderTests.WithThemeAsync(visual, dark, async root =>
        {
            var card = new AnimeCard { Anime = new Anime(1, "评分测试", null, [], null, null, "", 2026, 1, Score: 7.9) };
            root.Children.Add(card);
            root.UpdateLayout();
            await Task.Delay(20);
            Assert.Equal(new CornerRadius(radius), ((Border)card.FindName("ScoreBadge")).CornerRadius);
        });

    [Theory]
    [InlineData(DesktopVisualStyle.Paper, false)]
    [InlineData(DesktopVisualStyle.Paper, true)]
    [InlineData(DesktopVisualStyle.Fluent10, false)]
    [InlineData(DesktopVisualStyle.Fluent10, true)]
    [InlineData(DesktopVisualStyle.Fluent11, false)]
    [InlineData(DesktopVisualStyle.Fluent11, true)]
    public Task DailyPickScoreKeepsItsOwnFluentGeometryAndSharesPaperCorners(DesktopVisualStyle visual, bool dark)
        => DesktopInteractionRenderTests.WithThemeAsync(visual, dark, async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var tracking = new TrackingService(fixture.Factory);
            var state = new PersonalFeatureStateStore(fixture.Factory);
            var page = new CurrentSeasonPage(new Source(), new DragDropService(tracking, state, NullLogger<DragDropService>.Instance),
                tracking, state, fixture.Navigation, NullLogger<CurrentSeasonPage>.Instance);
            root.Children.Add(page);
            await Task.Delay(30);
            root.UpdateLayout();
            await page.PendingActivation;
            var image = (Image)page.FindName("DailyPickCover");
            var badge = ((Grid)VisualTreeHelper.GetParent(image)).Children.OfType<Border>().Single();
            Assert.Equal(visual == DesktopVisualStyle.Paper ? new CornerRadius(6) : new CornerRadius(8, 0, 0, 0), badge.CornerRadius);
        });

    [Fact]
    [Trait("Category", "Mechanism")]
    public void SmallActionsAndDailyPickScoreUseTheSharedDesktopResources()
    {
        var directory = Path.Combine(Repository(), "Plugins", "AniMeido.Plugin.Base.Desktop", "Views");
        var today = XDocument.Load(Path.Combine(directory, "TodayPage.xaml"));
        foreach (var label in new[] { "换一批", "清空" })
        {
            var button = Assert.Single(today.Descendants(), element => element.Name.LocalName == "Button" && (string?)element.Attribute("Content") == label);
            Assert.Equal("{StaticResource DesktopSmallActionButtonStyle}", (string?)button.Attribute("Style"));
            Assert.Null(button.Attribute("Padding"));
        }
        var calendar = XDocument.Load(Path.Combine(directory, "CurrentSeasonPage.xaml"));
        var badge = Assert.Single(calendar.Descendants(), element => element.Name.LocalName == "Border"
            && (string?)element.Attribute("Visibility") == "{x:Bind ViewModel.HasDailyPickScore, Mode=OneWay}");
        Assert.Equal("{StaticResource DesktopDailyPickScoreBadgeStyle}", (string?)badge.Attribute("Style"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExistingControlsFollowVisualAndLightDarkChangesWithoutLosingSelection(bool dark)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper", ThemeSetting = dark ? "Dark" : "Light" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var page = fixture.Mine(new Source());
            var checkbox = new CheckBox { IsChecked = true, Content = "保持已选" };
            var card = new AnimeCard { Anime = new Anime(1, "评分测试", null, [], null, null, "", 2026, 1, Score: 7.9) };
            var overlay = new StackPanel { Width = 150, HorizontalAlignment = HorizontalAlignment.Right, Children = { checkbox, card } };
            root.Children.Add(page);
            root.Children.Add(overlay);
            await Task.Delay(30);
            root.UpdateLayout();
            await page.PendingActivation;
            await Task.Delay(30);
            root.UpdateLayout();
            var selected = Children<Button>(page).First(button => button.Tag is TrackingStatusSection { IsSelected: true });
            var panel = (Grid)selected.Content;
            var selection = (Border)panel.Children[0];
            var marker = (Border)panel.Children[1];
            var box = Children<FrameworkElement>(checkbox).Single(element => element.Name == "NormalRectangle");
            var score = (Border)card.FindName("ScoreBadge");
            foreach (var visual in new[] { DesktopVisualStyle.Fluent10, DesktopVisualStyle.Fluent11, DesktopVisualStyle.Paper })
            {
                await theme.SetVisualStyleAsync(visual);
                root.UpdateLayout();
                await Task.Delay(20);
                Assert.True(((TrackingStatusSection)selected.Tag).IsSelected);
                Assert.Equal(Visibility.Visible, selection.Visibility);
                var nativeMarker = (double)Application.Current.Resources["NavigationViewSelectionIndicatorWidth"];
                output.WriteLine($"visual={visual}; dark={dark}; nativeMarkerWidth={nativeMarker}; actualHeight={marker.Height}");
                Assert.Equal(visual == DesktopVisualStyle.Paper ? 0 : nativeMarker, marker.Height);
                Assert.Equal(new CornerRadius(visual == DesktopVisualStyle.Paper ? 6 : 0), score.CornerRadius);
                Assert.True(VisualStateManager.GoToState(checkbox, "CheckedPointerOver", false));
                Assert.Equal(ColorOf((Brush)Application.Current.Resources["DesktopPrimaryButtonHoverBrush"]), ColorOf(Fill(box)));
                Assert.True(VisualStateManager.GoToState(checkbox, "CheckedNormal", false));
            }
            await theme.SetThemeAsync(dark ? ElementTheme.Light : ElementTheme.Dark);
            root.UpdateLayout();
            Assert.Equal(new CornerRadius(6), selection.CornerRadius);
            Assert.Equal(0, marker.Height);
            Assert.True(VisualStateManager.GoToState(checkbox, "CheckedPointerOver", false));
            Assert.Equal(ColorOf((Brush)Application.Current.Resources["DesktopPrimaryButtonHoverBrush"]), ColorOf(Fill(box)));
            Assert.Equal(new CornerRadius(6), score.CornerRadius);
        });

    private static Brush Fill(FrameworkElement element) => element is Shape shape ? shape.Fill : ((Border)element).Background;
    private static Windows.UI.Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static void Invoke(Button button) => typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Children<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void SetScale(XamlRoot root, double value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var tree = typeof(XamlRoot).GetProperty("VisualTree", flags)!.GetValue(root)!;
        var scale = tree.GetType().GetProperty("RootScale", flags)!.GetValue(tree)!;
        scale.GetType().GetMethod("SetTestOverride", flags)!.Invoke(scale, [(float)value]);
        typeof(XamlRoot).GetMethod("RaiseChangedEvent", flags)!.Invoke(root, null);
    }
    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
