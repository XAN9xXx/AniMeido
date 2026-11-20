using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.UI.Input.Preview.Injection;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopFinalFeedbackVisualTests
{
    [Theory]
    [InlineData("Fluent10", false, true)]
    [InlineData("Fluent10", true, true)]
    [InlineData("Fluent11", false, true)]
    [InlineData("Fluent11", true, true)]
    [InlineData("Paper", false, true)]
    [InlineData("Paper", true, true)]
    [InlineData("Fluent10", true, false)]
    [InlineData("Fluent11", false, false)]
    [InlineData("Paper", true, false)]
    public Task AnOldWorkShowsTheSavedCheckmarkInTheRenderedMenu(string style, bool dark, bool marked)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = style, ThemeSetting = dark ? "Dark" : "Light", FontStyle = "Serif" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var tracking = new TrackingService(fixture.Factory);
            if (marked) await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            var page = fixture.Details(new Source
            {
                Detail = (id, _) => Task.FromResult<Anime?>(Item(id) with
                {
                    AirDate = new DateOnly(2019, 1, 4), SeasonYear = 2019, SeasonMonth = 1,
                }),
            });
            await page.OnNavigatedToAsync(1);
            await AttachAsync(root, page);
            var button = (DropDownButton)page.FindName("DesktopTrackingButton");
            var menu = Assert.IsType<MenuFlyout>(button.Flyout);
            try
            {
                await page.PendingActivation;
                Assert.True(page.ViewModel.IsOldSeason);
                Assert.DoesNotContain(page.ViewModel.VisibleTrackingActions, action => action.Status == AnimeTrackingStatus.Watching);
                if (marked) Assert.Equal("追番中", button.Content);
                menu.ShowAt(button);
                var popup = Assert.IsAssignableFrom<FrameworkElement>(
                    VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot!).Last().Child);
                await UnoVisualEvidence.WaitForAsync(() => menu.Items[0] is { IsLoaded: true, ActualHeight: > 0 }
                    && popup.ActualHeight > 100);
                await UnoVisualEvidence.CaptureAsync(popup, "H-acceptance", $"old-work-{style}-{dark}-marked-{marked}");
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(popup);
                var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                var marks = menu.Items.OfType<ToggleMenuFlyoutItem>().Where(InkInCheckColumn).ToArray();
                if (marked)
                {
                    var current = Assert.Single(marks);
                    Assert.Equal("追番", current.Text);
                    Assert.True(current.IsChecked);
                    var accent = Assert.IsType<SolidColorBrush>(Application.Current.Resources["DesktopAccentBrush"]).Color;
                    Assert.True(AccentInk(current, 9, 29), $"勾应使用主题强调色 {accent}。");
                    Assert.False(AccentInk(current, 41, (int)current.ActualWidth - 10), "标签文字应保留原色。");
                    Assert.Contains(menu.Items.OfType<ToggleMenuFlyoutItem>(), item => item.Text == "补番");
                    Assert.Equal("取消标记", Assert.IsType<MenuFlyoutItem>(menu.Items[^1]).Text);
                    foreach (var state in new[] { "PointerOver", "Pressed", "Normal" })
                    {
                        Assert.True(VisualStateManager.GoToState(current, state, useTransitions: false));
                        await bitmap.RenderAsync(popup);
                        pixels = (await bitmap.GetPixelsAsync()).ToArray();
                        Assert.True(AccentInk(current, 9, 29), $"{state} 状态的勾应使用主题强调色 {accent}。");
                    }
                    if (style == "Paper" && dark)
                    {
                        await ActivateMenuItemAsync(current);
                        Assert.Equal(AnimeTrackingStatus.Watching, await tracking.GetStatusAsync(1));
                        menu.ShowAt(button);
                        var remove = Assert.IsType<MenuFlyoutItem>(menu.Items[^1]);
                        await ActivateMenuItemAsync(remove);
                        Assert.Null(await tracking.GetStatusAsync(1));
                    }

                    bool AccentInk(ToggleMenuFlyoutItem item, int from, int to)
                    {
                        var point = item.TransformToVisual(popup).TransformPoint(default(Point));
                        var left = (int)Math.Round(point.X);
                        var center = (int)Math.Round(point.Y + item.ActualHeight / 2);
                        for (var y = center - 7; y <= center + 7; y++)
                        for (var x = left + from; x <= left + to; x++)
                        {
                            var sample = (y * bitmap.PixelWidth + x) * 4;
                            if (Math.Abs(pixels[sample] - accent.B) <= 42
                                && Math.Abs(pixels[sample + 1] - accent.G) <= 42
                                && Math.Abs(pixels[sample + 2] - accent.R) <= 42
                                && Math.Max(pixels[sample], Math.Max(pixels[sample + 1], pixels[sample + 2]))
                                   - Math.Min(pixels[sample], Math.Min(pixels[sample + 1], pixels[sample + 2])) > 44)
                                return true;
                        }
                        return false;
                    }
                }
                else
                {
                    Assert.Empty(marks);
                    Assert.DoesNotContain(menu.Items.OfType<ToggleMenuFlyoutItem>(), item => item.Text == "追番");
                    Assert.DoesNotContain(menu.Items.OfType<MenuFlyoutItem>(), item => item.Text == "取消标记");
                }

                bool InkInCheckColumn(ToggleMenuFlyoutItem item)
                {
                    var point = item.TransformToVisual(popup).TransformPoint(default(Point));
                    var left = (int)Math.Round(point.X);
                    var center = (int)Math.Round(point.Y + item.ActualHeight / 2);
                    var count = 0;
                    for (var y = center - 6; y <= center + 6; ++y)
                    for (var x = left + 9; x <= left + 29; ++x)
                    {
                        var sample = (y * bitmap.PixelWidth + x) * 4;
                        var baseline = (y * bitmap.PixelWidth + left + 33) * 4;
                        if (Enumerable.Range(0, 3).Any(channel => Math.Abs(pixels[sample + channel] - pixels[baseline + channel]) > 60))
                            ++count;
                    }
                    return count >= 5;
                }

                async Task ActivateMenuItemAsync(MenuFlyoutItem item)
                {
                    await UnoVisualEvidence.WaitForAsync(() => item.IsLoaded);
                    Assert.True(item.Focus(FocusState.Keyboard));
                    var keyboard = Assert.IsType<InputInjector>(InputInjector.TryCreate());
                    keyboard.InjectKeyboardInput([
                        new() { VirtualKey = (ushort)Windows.System.VirtualKey.Enter },
                        new() { VirtualKey = (ushort)Windows.System.VirtualKey.Enter, KeyOptions = InjectedInputKeyOptions.KeyUp },
                    ]);
                    await UnoVisualEvidence.WaitForAsync(() =>
                        !VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot!).Any(p => p.IsOpen));
                    await page.PendingOperation;
                }
            }
            finally { menu.Hide(); await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Theory]
    [InlineData("Fluent10", false)]
    [InlineData("Fluent10", true)]
    [InlineData("Fluent11", false)]
    [InlineData("Fluent11", true)]
    [InlineData("Paper", false)]
    [InlineData("Paper", true)]
    public Task MarkingMenuHasOneSelectionMarkAndASeparatePlainRemoval(string style, bool dark)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = style, ThemeSetting = dark ? "Dark" : "Light", FontStyle = "Serif" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            await new TrackingService(fixture.Factory).SetStatusAsync(1, AnimeTrackingStatus.Watching);
            var page = fixture.Details(new Source());
            await page.OnNavigatedToAsync(1);
            await AttachAsync(root, page);
            var button = (DropDownButton)page.FindName("DesktopTrackingButton");
            var menu = Assert.IsType<MenuFlyout>(button.Flyout);
            try
            {
                await page.PendingActivation;
                root.UpdateLayout();
                menu.ShowAt(button);
                Assert.Single(menu.Items.OfType<ToggleMenuFlyoutItem>(), item => item.IsChecked);
                Assert.All(menu.Items.OfType<ToggleMenuFlyoutItem>(), item => Assert.Null(item.Icon));
                Assert.IsType<MenuFlyoutSeparator>(menu.Items[^2]);
                Assert.Equal("取消标记", Assert.IsType<MenuFlyoutItem>(menu.Items[^1]).Text);
                var opened = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot!);
                var popupContent = Assert.IsAssignableFrom<FrameworkElement>(opened.Last().Child);
                await UnoVisualEvidence.WaitForAsync(() => menu.Items[0] is { IsLoaded: true, ActualHeight: > 0 }
                    && popupContent.ActualHeight > 150);
                await UnoVisualEvidence.CaptureAsync(popupContent, "B-feedback", $"mark-menu-{style}-{dark}");
            }
            finally { menu.Hide(); await DetachAsync(root, page); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PaperRecommendationRefreshKeepsResultLayoutAndStacksFeedbackOutsideThePage(bool dark)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper", ThemeSetting = dark ? "Dark" : "Light", FontStyle = "Serif" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            root.Background = (Brush)Application.Current.Resources["DesktopPageBrush"];
            var source = new Source { Seasonal = _ => Task.FromResult(new List<Anime> { Item(21), Item(22), Item(23) }) };
            var page = fixture.Recommendation(source, State());
            root.Children.Insert(0, page);
            try
            {
                await UnoVisualEvidence.WaitForAsync(() => page.IsLoaded);
                await page.PendingActivation;
                await page.RefreshAsync(message => theme.ShowFeedback(message));
                Assert.False(((InfoBar)page.FindName("StatusInfoBar")).IsOpen);
                var message = Assert.IsType<string>(page.ViewModel.Message);
                var overlay = root.Children.OfType<Grid>().Single();
                theme.ShowFeedback("已保留你刚才选择的标签。");
                theme.ShowFeedback("暂时无法获取作品资料，已保留上次结果。", error: true);
                root.UpdateLayout();
                var success = await ToastTextAsync(message);
                var second = await ToastTextAsync("已保留你刚才选择的标签。");
                var error = await ToastTextAsync("暂时无法获取作品资料，已保留上次结果。");
                Assert.True(error.TransformToVisual(root).TransformPoint(default).Y > 400);
                Assert.True(success.TransformToVisual(root).TransformPoint(default).Y < 170);
                Assert.True(second.TransformToVisual(root).TransformPoint(default).Y < 170);
                await UnoVisualEvidence.CaptureAsync(root, "B-feedback", $"recommendation-toasts-Paper-{dark}");

                async Task<TextBlock> ToastTextAsync(string value)
                {
                    TextBlock? text = null;
                    await UnoVisualEvidence.WaitForAsync(() => (text = UnoVisualEvidence.Descendants(overlay)
                        .OfType<TextBlock>().FirstOrDefault(item => item.IsLoaded && item.Text == value)) is not null);
                    return text!;
                }
            }
            finally { await DetachAsync(root, page); await page.PendingTags; }
        });
}
