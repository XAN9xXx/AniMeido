using AniMeido.App.Services;
using AniMeido.App.Views;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Reflection;
using Windows.Foundation;
using Windows.UI.Input.Preview.Injection;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopFinalSettingsVisualTests
{
    [Theory]
    [InlineData("Fluent10", false)]
    [InlineData("Fluent10", true)]
    [InlineData("Fluent11", false)]
    [InlineData("Fluent11", true)]
    [InlineData("Paper", false)]
    [InlineData("Paper", true)]
    public Task RealSettingsUseTheVisibleModeAndLightweightAboutLinks(string style, bool dark)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings
            {
                VisualStyle = style, ThemeSetting = dark ? "Dark" : "Light",
                FontStyle = style == "Paper" ? "Serif" : "Modern",
            });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var page = new AppSettingsPage(null!, null!, store, theme, null!, new InstalledAppContext(null), null!);
            page.Loaded -= SettingsHandler(page, "OnPageLoaded");
            page.Unloaded -= SettingsHandler(page, "OnPageUnloaded");
            root.Children.Add(page);
            try
            {
                await Task.Delay(40);
                root.UpdateLayout();
                var brightness = (DesktopChoice)page.FindName("ThemeCombo");
                Assert.Equal(dark ? ElementTheme.Dark : ElementTheme.Light, page.ActualTheme);
                Assert.Equal(dark ? 1 : 0, brightness.SelectedIndex);
                var choice = (DesktopChoice)page.FindName("CatalogCardsChoice");
                var controls = Assert.IsType<Grid>(choice.Content).Children;
                var segments = controls.OfType<ListBox>().Single();
                var dropDown = controls.OfType<ComboBox>().Single();
                Assert.Equal(style == "Paper", segments.Visibility == Visibility.Visible);
                Assert.Equal(style != "Paper", dropDown.Visibility == Visibility.Visible);
                if (style == "Paper")
                {
                    var first = Assert.IsType<ListBoxItem>(segments.ContainerFromIndex(0));
                    var second = Assert.IsType<ListBoxItem>(segments.ContainerFromIndex(1));
                    Assert.Equal(first.ActualWidth, second.ActualWidth, 1);
                    Assert.InRange(first.ActualWidth + second.ActualWidth, segments.ActualWidth - 12, segments.ActualWidth);
                    var mode = Assert.IsType<Grid>(brightness.Content).Children.OfType<ListBox>().Single();
                    mode.SelectedIndex = dark ? 0 : 1;
                    await WaitForAsync(() => page.ActualTheme == (dark ? ElementTheme.Light : ElementTheme.Dark));
                    mode.SelectedIndex = dark ? 1 : 0;
                    await WaitForAsync(() => page.ActualTheme == (dark ? ElementTheme.Dark : ElementTheme.Light));
                    await DesktopUiWork.DrainAsync();
                    Assert.Equal(dark ? "Dark" : "Light", (await store.LoadAsync()).ThemeSetting);
                }
                await UnoVisualEvidence.CaptureAsync(page, "A-settings", $"appearance-{style}-{dark}");
                if (style == "Paper")
                {
                    var item = Assert.IsType<ListBoxItem>(segments.ContainerFromIndex(1));
                    var mouse = Assert.IsType<InputInjector>(InputInjector.TryCreate(root));
                    var hit = item.TransformToVisual(root).TransformPoint(new Point(item.ActualWidth / 2, item.ActualHeight / 2));
                    mouse.InjectMouseInput([new() { MouseOptions = InjectedInputMouseOptions.Move, DeltaX = (int)hit.X, DeltaY = (int)hit.Y }]);
                    await UnoVisualEvidence.CaptureAsync(page, "A-settings", $"appearance-Paper-{dark}-hover");
                    var xamlRoot = root.XamlRoot!;
                    try
                    {
                        var preview = Assert.IsType<Expander>(FindExpander(page));
                        preview.IsExpanded = true;
                        foreach (var scale in new[] { 125, 150 })
                        {
                            UnoVisualEvidence.SetScale(xamlRoot, scale / 100f);
                            Assert.Equal(scale / 100d, xamlRoot.RasterizationScale);
                            await UnoVisualEvidence.CaptureAsync(page, "A-settings", $"appearance-Paper-{dark}-Chinese-{scale}");
                        }
                    }
                    finally { UnoVisualEvidence.SetScale(xamlRoot, 0); }
                }
                page.SetDesktopSection("about");
                root.UpdateLayout();
                Assert.IsType<HyperlinkButton>(page.FindName("GitHubButton"));
                Assert.IsType<HyperlinkButton>(page.FindName("WebsiteButton"));
                await UnoVisualEvidence.CaptureAsync(page, "A-settings", $"about-{style}-{dark}");
            }
            finally { root.Children.Remove(page); await DesktopUiWork.DrainAsync(); }
        });

    [Theory]
    [InlineData("Fluent10", false)]
    [InlineData("Fluent10", true)]
    [InlineData("Fluent11", false)]
    [InlineData("Fluent11", true)]
    [InlineData("Paper", false)]
    [InlineData("Paper", true)]
    public Task DragPreviewDistinguishesActionsAndKeepsDisabledRegionsEditable(string style, bool dark)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = style, ThemeSetting = dark ? "Dark" : "Light", FontStyle = "Serif" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var page = new DragZoneSettingsPage(new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragZoneSettingsPage>.Instance);
            root.Children.Add(page);
            try
            {
                await WaitForAsync(() => page.IsLoaded);
                await page.PendingActivation;
                root.UpdateLayout();
                var preview = (Canvas)page.FindName("PreviewCanvas");
                var zones = preview.Children.OfType<Border>().ToArray();
                Assert.Equal(4, zones.Length);
                Assert.Equal(4, zones.Select(zone => Assert.IsType<SolidColorBrush>(zone.Background).Color).Distinct().Count());
                var disabled = Assert.Single(zones, zone => zone.Tag as string == "br");
                Assert.True(disabled.IsTabStop && disabled.IsHitTestVisible);
                Assert.Equal("禁用", Assert.IsType<TextBlock>(disabled.Child).Text);
                await UnoVisualEvidence.CaptureAsync(page, "A-settings", $"drag-{style}-{dark}");
            }
            finally { root.Children.Remove(page); await page.PendingOperation; }
        });

    private static RoutedEventHandler SettingsHandler(AppSettingsPage page, string name)
        => (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), page,
            typeof(AppSettingsPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!);

    private static Expander? FindExpander(DependencyObject node)
    {
        if (node is Expander expander) return expander;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); ++i)
            if (FindExpander(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
        return null;
    }

    internal static async Task WaitForAsync(Func<bool> done)
    {
        for (var i = 0; i < 150 && !done(); ++i) await Task.Delay(16);
        Assert.True(done());
    }
}
