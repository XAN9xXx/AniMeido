using AniMeido.App.Controls;
using AniMeido.App.Services;
using AniMeido.App.Themes;
using AniMeido.App.Views;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopAcceptanceInputTests
{
    [Fact]
    public Task PaperButtonsOnANewPageStartAtRestAndStillRespondToMouseAndKeyboard()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper", ThemeSetting = "Dark" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var resources = Application.Current.Resources;
            resources.MergedDictionaries.Add(new DesktopShellResources());
            var frame = new Frame { Content = new Page { Content = new TextBlock { Text = "上一页" } } };
            root.Children.Add(frame);
            var mouse = new DesktopTestMouse(root);
            mouse.MoveTo(root, 20, 660);

            var action = new Button { Style = (Style)resources["DesktopButtonStyle"], Content = "普通操作" };
            var section = new ToggleButton { Style = (Style)resources["DesktopUnderlinedSectionTabStyle"], Content = "外观", IsChecked = true };
            var nav = new DesktopShellNavigationItem("番剧库", "\uE8F1")
            { Style = (Style)resources["DesktopShellNavigationStyle"], IsChecked = true };
            nav.ApplyAppearance(DesktopVisualStyle.Paper, true, resources);
            var stack = new StackPanel { Children = { action, section, nav }, Width = 240, Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(12) };
            var page = new Page { Content = stack, Background = (Brush)resources["DesktopPageBrush"] };
            frame.Content = page;
            await WaitForAsync(() => nav.ActualWidth > 0 && action.IsLoaded);
            var actionSurface = Find<ContentPresenter>(action, "Surface");
            var sectionSurface = Find<ContentPresenter>(section, "Surface");
            var navSurface = Find<Border>(nav, "NavigationSurface");
            await UnoVisualEvidence.CaptureAsync(page, "E-acceptance", "paper-buttons-00-mount");
            Assert.Null(actionSurface.BackgroundTransition);
            Assert.Null(sectionSurface.BackgroundTransition);
            Assert.Null(navSurface.BackgroundTransition);
            Assert.Equal(((SolidColorBrush)action.Background).Color, ((SolidColorBrush)actionSurface.Background).Color);
            Assert.Equal(((SolidColorBrush)resources["DesktopCheckedButtonBrush"]).Color, ((SolidColorBrush)sectionSurface.Background).Color);
            Assert.Equal(((SolidColorBrush)resources["DesktopSelectedBrush"]).Color, ((SolidColorBrush)navSurface.Background).Color);

            var clicks = 0;
            action.Click += (_, _) => clicks++;
            mouse.MoveTo(action);
            await WaitForAsync(() => actionSurface.BackgroundTransition is not null);
            await UnoVisualEvidence.CaptureAsync(page, "E-acceptance", "paper-buttons-01-real-hover");
            mouse.Click(action);
            await WaitForAsync(() => clicks == 1);
            mouse.MoveTo(root, 20, 660);
            Assert.True(action.Focus(FocusState.Keyboard));
            mouse.Key(Windows.System.VirtualKey.Space);
            await WaitForAsync(() => clicks == 2);
            await UnoVisualEvidence.CaptureAsync(page, "E-acceptance", "paper-buttons-02-keyboard-focus");
            frame.Content = new Page();
            await WaitForAsync(() => !action.IsLoaded);
            frame.Content = page;
            await WaitForAsync(() => action.IsLoaded);
            Assert.Null(actionSurface.BackgroundTransition);
        });

    [Fact]
    public Task ClickingPaperFontSegmentsUpdatesTheSelectionAndRenderedPreviewAcrossSettingsVisitsAndReload()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper", ThemeSetting = "Dark", FontStyle = "System" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var frame = new Frame();
            root.Children.Add(frame);
            var page = CreatePage(store, theme);
            frame.Content = page;
            await WaitForAsync(() => page.IsLoaded);
            var mouse = new DesktopTestMouse(root);
            var (font, list) = Choice(page, "FontStyleChoice");
            Assert.Equal(1, font.SelectedIndex);
            Find<Expander>(page).IsExpanded = true;
            var english = FindText(page, "March comes in like a lion");
            var chinese = FindText(page, "桐山零是一名独自生活的高中生，也是一位职业将棋棋士。");
            await WaitForAsync(() => chinese.ActualWidth > 0);
            var beforeEnglish = await PixelsAsync(english);
            var beforeChinese = await PixelsAsync(chinese);
            await UnoVisualEvidence.CaptureAsync(page, "E-acceptance", "paper-font-00-modern-before");

            mouse.Click(Item(list, 2));
            var afterClick = Describe();
            await WaitForAsync(() => theme.CurrentAppearance.FontStyle == DesktopFontStyle.Serif);
            await DesktopUiWork.DrainAsync();
            Assert.Equal(2, font.SelectedIndex);
            Assert.Equal(2, list.SelectedIndex);
            Assert.True(Item(list, 2).IsSelected, $"Click: {afterClick}; saved: {Describe()}");
            Assert.False(Item(list, 1).IsSelected);
            Assert.Equal("Serif", (await store.LoadAsync()).FontStyle);
            Assert.Contains("DesktopSerif.ttf", english.FontFamily.Source);
            Assert.Contains("DesktopSerif.ttf", chinese.FontFamily.Source);
            var serifEnglish = await PixelsAsync(english);
            var serifChinese = await PixelsAsync(chinese);
            Assert.False(beforeEnglish.SequenceEqual(serifEnglish));
            Assert.False(beforeChinese.SequenceEqual(serifChinese));
            var selectedBrush = ((SolidColorBrush)Application.Current.Resources["DesktopSegmentSelectedBrush"]).Color;
            await WaitForAsync(() => SelectionAt(list, 2, selectedBrush));
            await UnoVisualEvidence.CaptureAsync(page, "E-acceptance", "paper-font-01-serif-after-click");
            mouse.MoveTo(Item(list, 1));
            await WaitForAsync(() => Find<Border>(Item(list, 1), "SegmentSurface").BorderBrush ==
                Application.Current.Resources["DesktopSegmentHoverBrush"]);
            Assert.Equal(0, ColorOfSegment(Item(list, 1)).A);
            Assert.True(SelectionAt(list, 2, selectedBrush));
            await UnoVisualEvidence.CaptureAsync(page, "E-acceptance", "paper-font-04-serif-with-modern-hover");

            mouse.Click(Item(list, 0));
            await WaitForAsync(() => theme.CurrentAppearance.FontStyle == DesktopFontStyle.Classic);
            Assert.True(Item(list, 0).IsSelected);
            mouse.Click(Item(list, 1));
            await WaitForAsync(() => theme.CurrentAppearance.FontStyle == DesktopFontStyle.System);
            Assert.True(Item(list, 1).IsSelected);

            var (brightness, brightnessList) = Choice(page, "ThemeCombo");
            mouse.Click(Item(brightnessList, 0));
            await WaitForAsync(() => page.ActualTheme == ElementTheme.Light);
            Assert.Equal(0, brightness.SelectedIndex);
            mouse.Click(Item(brightnessList, 1));
            await WaitForAsync(() => page.ActualTheme == ElementTheme.Dark);
            mouse.Click(Item(brightnessList, 2));
            await WaitForAsync(() => theme.GetCurrentTheme() == ElementTheme.Default);
            Assert.True(Item(brightnessList, 2).IsSelected);
            mouse.Click(Item(brightnessList, 1));
            await WaitForAsync(() => page.ActualTheme == ElementTheme.Dark && theme.GetCurrentTheme() == ElementTheme.Dark);

            var (cards, cardList) = Choice(page, "CatalogCardsChoice");
            mouse.Click(Item(cardList, 1));
            await WaitForAsync(() => theme.CurrentAppearance.CatalogCards == DesktopCatalogCardStyle.Detailed);
            Assert.Equal(1, cards.SelectedIndex);
            Assert.True(Item(cardList, 1).IsSelected);
            await DesktopUiWork.DrainAsync();
            Assert.Equal("Detailed", (await store.LoadAsync()).CatalogCardStyle);
            mouse.Click(Item(cardList, 0));
            await WaitForAsync(() => theme.CurrentAppearance.CatalogCards == DesktopCatalogCardStyle.Cover);
            var (style, styleList) = Choice(page, "VisualStyleChoice");
            mouse.Click(Item(styleList, 1));
            await WaitForAsync(() => theme.CurrentAppearance.VisualStyle == DesktopVisualStyle.Fluent11);
            Assert.Equal(1, style.SelectedIndex);
            await theme.SetVisualStyleAsync(DesktopVisualStyle.Paper);
            await WaitForAsync(() => list.Visibility == Visibility.Visible);
            mouse.Click(Item(list, 2));
            await WaitForAsync(() => theme.CurrentAppearance.FontStyle == DesktopFontStyle.Serif);
            await DesktopUiWork.DrainAsync();

            frame.Content = new Page();
            await WaitForAsync(() => !page.IsLoaded);
            frame.Content = page;
            await WaitForAsync(() => page.IsLoaded && Item(list, 2).IsSelected);
            await UnoVisualEvidence.CaptureAsync(page, "E-acceptance", "paper-font-02-serif-return");
            frame.Content = new Page();
            await WaitForAsync(() => !page.IsLoaded);
            await theme.DisposeAsync();

            var reopenedStore = new DesktopSettingsStore(fixture);
            await using var restarted = new ThemeService(reopenedStore);
            await restarted.InitializeThemeAsync(root);
            var reopenedPage = CreatePage(reopenedStore, restarted);
            frame.Content = reopenedPage;
            await WaitForAsync(() => reopenedPage.IsLoaded);
            var (reopened, reopenedList) = Choice(reopenedPage, "FontStyleChoice");
            Assert.Equal(2, reopened.SelectedIndex);
            Assert.Equal(DesktopFontStyle.Serif, restarted.CurrentAppearance.FontStyle);
            Assert.True(Item(reopenedList, 2).IsSelected);
            await WaitForAsync(() => SelectionAt(reopenedList, 2, selectedBrush));
            Find<Expander>(reopenedPage).IsExpanded = true;
            await UnoVisualEvidence.CaptureAsync(reopenedPage, "E-acceptance", "paper-font-03-new-service");

            string Describe() => $"index={list.SelectedIndex}; item={((ContentControl?)list.SelectedItem)?.Content ?? list.SelectedItem}; flags={string.Join(",", Enumerable.Range(0, 3).Select(i => $"{Item(list, i).Content}:{Item(list, i).IsSelected}"))}";
        });

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public Task PaperSettingsShowOneSlidingSelectionOnActualInput(string brightness)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper", ThemeSetting = brightness, FontStyle = "System" });
            var motionEnabled = true;
            await using var theme = new ThemeService(store, null, () => motionEnabled);
            await theme.InitializeThemeAsync(root);
            var page = CreatePage(store, theme);
            root.Children.Add(page);
            await WaitForAsync(() => page.IsLoaded);
            var (_, list) = Choice(page, "FontStyleChoice");
            root.UpdateLayout();
            var pill = Find<Border>(list, "SegmentSelection");
            Assert.False(pill.IsHitTestVisible);
            var mouse = new DesktopTestMouse(root);
            Assert.InRange(Slot(), .99, 1.01);
            await UnoVisualEvidence.CaptureAsync(list, "I-final-motion", $"settings-choice-{brightness}-00-rest");
            mouse.Click(Item(list, 2));
            Assert.Equal(2, list.SelectedIndex);
            var positions = new List<double>();
            for (var frame = 1; frame <= 6; ++frame)
            {
                await Task.Delay(18);
                positions.Add(Slot());
                await UnoVisualEvidence.CaptureAsync(list, "I-final-motion", $"settings-choice-{brightness}-{frame:00}-slide");
            }
            Assert.Contains(positions, slot => slot is > 1.02 and < 1.98);
            Assert.All(positions.Zip(positions.Skip(1)), pair => Assert.True(pair.Second >= pair.First - .01));
            await WaitForAsync(() => Math.Abs(Slot() - 2) < .01);
            Assert.Equal("Serif", (await store.LoadAsync()).FontStyle);
            await UnoVisualEvidence.CaptureAsync(list, "I-final-motion", $"settings-choice-{brightness}-07-settled");

            mouse.Click(Item(list, 0));
            motionEnabled = false;
            theme.RefreshSystemMotionPreference();
            Assert.InRange(Math.Abs(Slot()), 0, .01);
            Assert.True(Item(list, 1).Focus(FocusState.Keyboard));
            mouse.Key(Windows.System.VirtualKey.Space);
            await WaitForAsync(() => list.SelectedIndex == 1);
            Assert.Equal(1, list.SelectedIndex);
            Assert.InRange(Math.Abs(Left(pill) - Left(Item(list, 1))), 0, 1);
            await UnoVisualEvidence.CaptureAsync(list, "I-final-motion", $"settings-choice-{brightness}-08-reduced");
            list.IsEnabled = false;
            Assert.Equal(.45, pill.Opacity);
            double Left(FrameworkElement element) => element.TransformToVisual(list).TransformPoint(default).X;
            double Slot() => (Left(pill) - Left(Item(list, 0))) / (Left(Item(list, 1)) - Left(Item(list, 0)));
        });

    private static AppSettingsPage CreatePage(DesktopSettingsStore store, ThemeService theme)
    {
        var page = new AppSettingsPage(null!, null!, store, theme, null!, new InstalledAppContext(null), null!);
        page.Loaded -= Handler("OnPageLoaded");
        page.Unloaded -= Handler("OnPageUnloaded");
        return page;
        RoutedEventHandler Handler(string name) => (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), page,
            typeof(AppSettingsPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!);
    }

    private static (DesktopChoice Choice, ListBox List) Choice(AppSettingsPage page, string name)
    {
        var choice = Assert.IsType<DesktopChoice>(page.FindName(name));
        return (choice, Assert.IsType<Grid>(choice.Content).Children.OfType<ListBox>().Single());
    }
    private static ListBoxItem Item(ListBox list, int index) => Assert.IsAssignableFrom<ListBoxItem>(list.ContainerFromIndex(index));
    private static TextBlock FindText(DependencyObject parent, string value)
        => Descendants(parent).OfType<TextBlock>().Single(text => text.Text == value);
    private static Windows.UI.Color ColorOfSegment(ListBoxItem item)
        => ((SolidColorBrush)Find<Border>(item, "SegmentSurface").Background).Color;
    private static bool SelectionAt(ListBox list, int index, Windows.UI.Color color)
    {
        var pill = Find<Border>(list, "SegmentSelection");
        return pill.Visibility == Visibility.Visible && ((SolidColorBrush)pill.Background).Color == color
            && Math.Abs(pill.TransformToVisual(list).TransformPoint(default).X
                - Item(list, index).TransformToVisual(list).TransformPoint(default).X) < 1;
    }
    private static T Find<T>(DependencyObject parent, string? name = null) where T : FrameworkElement
        => Descendants(parent).OfType<T>().First(element => name is null || element.Name == name);
    private static async Task<byte[]> PixelsAsync(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        Assert.Contains(pixels, value => value != 0);
        return pixels;
    }
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject element)
        => UnoVisualEvidence.Descendants(element);

    private static Task WaitForAsync(Func<bool> condition) => DesktopFinalSettingsVisualTests.WaitForAsync(condition);
}
