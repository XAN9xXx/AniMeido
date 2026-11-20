using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Input.Preview.Injection;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopFinalMotionVisualTests
{
    [Theory]
    [InlineData("Paper")]
    [InlineData("Fluent10")]
    [InlineData("Fluent11")]
    public Task ActualTrackingMenuArrowFollowsTheFlyoutAndKeepsWindowsStill(string style)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = style, ThemeSetting = "Dark" });
            var motionEnabled = true;
            await using var theme = new ThemeService(store, null, () => motionEnabled);
            await theme.InitializeThemeAsync(root);
            var page = fixture.Details(new Source());
            await page.OnNavigatedToAsync(1);
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                var button = (DropDownButton)page.FindName("DesktopTrackingButton");
                var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
                var arrow = Find<FrameworkElement>(button, element => element.Name == "ChevronIcon");
                var mouse = new DesktopTestMouse(root);
                await UnoVisualEvidence.CaptureAsync(button, "I-final-motion", $"tracking-arrow-{style}-00-rest");
                mouse.Click(button);
                await UnoVisualEvidence.WaitForAsync(() => flyout.IsOpen);
                if (style == "Paper")
                {
                    var rotation = Assert.IsType<RotateTransform>(Find<FontIcon>(arrow, item => item.Glyph == "\uE70D").RenderTransform);
                    await CaptureAngles(rotation, 0, 180, "open");
                    mouse.Key(Windows.System.VirtualKey.Escape);
                    await UnoVisualEvidence.WaitForAsync(() => !flyout.IsOpen);
                    await CaptureAngles(rotation, 180, 0, "close");
                    motionEnabled = false;
                    theme.RefreshSystemMotionPreference();
                    double? openedAngle = null;
                    flyout.Opened += (_, _) => openedAngle = rotation.Angle;
                    mouse.Click(button);
                    await UnoVisualEvidence.WaitForAsync(() => openedAngle.HasValue);
                    Assert.Equal(180, openedAngle);
                    double? closedAngle = null;
                    flyout.Closed += (_, _) => closedAngle = rotation.Angle;
                    flyout.Hide();
                    await UnoVisualEvidence.WaitForAsync(() => closedAngle.HasValue);
                    Assert.Equal(0, closedAngle);
                }
                else
                {
                    await Task.Delay(180);
                    Assert.True(arrow.RenderTransform is not RotateTransform fluent || fluent.Angle == 0);
                    flyout.Hide();
                }

                async Task CaptureAngles(RotateTransform rotation, double start, double end, string name)
                {
                    var angles = new List<double>();
                    for (var i = 1; i <= 6; i++)
                    {
                        await Task.Delay(18);
                        angles.Add(rotation.Angle);
                        await UnoVisualEvidence.CaptureAsync(button, "I-final-motion", $"tracking-arrow-{style}-{name}-{i:00}");
                    }
                    Assert.Contains(angles, angle => angle is > 2 and < 178);
                    await UnoVisualEvidence.WaitForAsync(() => Math.Abs(rotation.Angle - end) < .1);
                    Assert.All(angles.Zip(angles.Skip(1)), pair => Assert.True((pair.Second - pair.First) * (end - start) >= 0));
                }
            }
            finally { await DetachAsync(root, page); }
        });

    [Fact]
    public Task SeasonPickerAndMineDisclosureFollowTheirActualButtons()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            await using var theme = await PaperThemeAsync(root, fixture);
            Application.Current.Resources["DesktopMotionEnabled"] = true;
            var tracking = new TrackingService(fixture.Factory);
            var pointer = new DesktopTestMouse(root);
            var source = new Source { Seasonal = _ => Task.FromResult(new List<Anime> { Item(1) }) };
            var catalog = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance);
            try
            {
                await AttachAsync(root, catalog);
                var button = (Button)catalog.FindName("SeasonPickerButton");
                var popup = Assert.IsType<Flyout>(button.Flyout);
                await CheckAsync(button, () => popup.IsOpen, popup.Hide, "season");
            }
            finally { await DetachAsync(root, catalog); }
            var mine = fixture.Mine(source);
            try
            {
                await AttachAsync(root, mine);
                await mine.PendingActivation;
                var button = (Button)mine.FindName("HiddenToggle");
                // 使用同一个注入器，保留切页前的指针坐标。
                await CheckAsync(button, () => mine.ViewModel.IsHiddenExpanded, () => pointer.Click(button), "mine");
            }
            finally { await DetachAsync(root, mine); }

            async Task CheckAsync(Button owner, Func<bool> open, Action close, string name)
            {
                var angle = Assert.IsType<RotateTransform>(Find<FontIcon>(owner, icon => icon.Glyph == "\uE70D").RenderTransform);
                pointer.Click(owner);
                await UnoVisualEvidence.WaitForAsync(open);
                var angles = new List<double>();
                for (var i = 1; i <= 6; ++i)
                {
                    await Task.Delay(18);
                    angles.Add(angle.Angle);
                    await UnoVisualEvidence.CaptureAsync(owner, "I-final-motion", $"{name}-arrow-{i:00}-open");
                }
                Assert.Contains(angles, value => value is > 2 and < 178);
                await UnoVisualEvidence.WaitForAsync(() => Math.Abs(angle.Angle - 180) < .1);
                close();
                await UnoVisualEvidence.WaitForAsync(() => !open() && Math.Abs(angle.Angle) < .1);
            }
        });

    [Fact]
    public Task PaperDisclosuresRotateThroughIntermediateAnglesAndRespectReducedMotion()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            await using var theme = await PaperThemeAsync(root, fixture);
            Application.Current.Resources["DesktopMotionEnabled"] = true;
            var expander = new Expander { Header = "作品资料与使用说明", Content = "显示详细资料", HorizontalAlignment = HorizontalAlignment.Stretch };
            var combo = new ComboBox { Header = "选择排序", MinWidth = 180, ItemsSource = new[] { "按日期", "按评分" }, SelectedIndex = 0 };
            var panel = new StackPanel { Width = 400, Spacing = 16, Padding = new Thickness(16),
                Background = (Brush)Application.Current.Resources["DesktopPageBrush"], Children = { expander, combo } };
            root.Children.Add(panel);
            await DesktopFinalSettingsVisualTests.WaitForAsync(() => expander.IsLoaded && combo.IsLoaded);
            root.UpdateLayout();
            var arrow = Find<FontIcon>(expander, icon => icon.Glyph == "\uE70D");
            var rotation = Assert.IsType<RotateTransform>(arrow.RenderTransform);
            var mouse = new DesktopTestMouse(root);
            await UnoVisualEvidence.CaptureAsync(panel, "C-motion", "disclosure-00-rest");
            mouse.Click(Find<ToggleButton>(expander, item => item.Name == "ExpanderHeader"));
            var angles = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(23);
                angles.Add(rotation.Angle);
                await UnoVisualEvidence.CaptureAsync(panel, "C-motion", $"disclosure-{i + 1:00}-open");
            }
            Assert.Contains(angles, angle => angle is > 2 and < 178);
            await DesktopFinalSettingsVisualTests.WaitForAsync(() => Math.Abs(rotation.Angle - 180) < 0.1);
            var comboRotation = Assert.IsType<RotateTransform>(Find<FontIcon>(combo, icon => icon.Glyph == "\uE70D").RenderTransform);
            mouse.Click(combo);
            var comboAngles = new List<double>();
            for (var i = 1; i <= 6; i++)
            {
                await Task.Delay(18);
                comboAngles.Add(comboRotation.Angle);
                await UnoVisualEvidence.CaptureAsync(combo, "I-final-motion", $"combo-arrow-{i:00}-open");
            }
            Assert.True(combo.IsDropDownOpen);
            Assert.Contains(comboAngles, angle => angle is > 2 and < 178);
            await DesktopFinalSettingsVisualTests.WaitForAsync(() => Math.Abs(comboRotation.Angle - 180) < 0.1);
            combo.IsDropDownOpen = false;
            Application.Current.Resources["DesktopMotionEnabled"] = false;
            expander.IsExpanded = false;
            Assert.Equal(0, rotation.Angle, 1);
            await UnoVisualEvidence.CaptureAsync(panel, "C-motion", "disclosure-07-reduced");
        });

    [Fact]
    public Task SavingATagKeepsBothActualButtonsAndUpdatesOnlyTheirStatus()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            await using var theme = await PaperThemeAsync(root, fixture);
            var page = fixture.Details(new Source
            {
                Tags = (_, _) => Task.FromResult(new List<Tag> { new("奇幻"), new("成长与漫长的冬日旅程") }),
            });
            await page.OnNavigatedToAsync(12);
            try
            {
                await AttachAsync(root, page); await page.PendingActivation;
                var container = (StackPanel)page.FindName("TagContainer");
                var first = Assert.IsType<Button>(container.Children[0]);
                var second = Assert.IsType<Button>(container.Children[1]);
                await UnoVisualEvidence.CaptureAsync(page, "C-motion", "detail-tag-00-unsaved");
                await page.ToggleSavedTagAsync("奇幻");
                Assert.Same(first, container.Children[0]);
                Assert.Same(second, container.Children[1]);
                Assert.Equal("已收藏", AutomationProperties.GetItemStatus(first));
                Assert.Equal("未收藏", AutomationProperties.GetItemStatus(second));
                await UnoVisualEvidence.CaptureAsync(page, "C-motion", "detail-tag-01-saved");
            }
            finally { await DetachAsync(root, page); }
        });

    [Fact]
    public Task NewlyVisibleTodayBroadcastStartsAtRestAndStillRespondsToActualHover()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            await using var theme = await PaperThemeAsync(root, fixture);
            Application.Current.Resources["DesktopMotionEnabled"] = true;
            await new TrackingService(fixture.Factory).SetStatusAsync(1, AnimeTrackingStatus.Watching);
            var gate = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = fixture.Today(new Source { Schedule = (_, _) => gate.Task });
            var mouse = Assert.IsType<InputInjector>(InputInjector.TryCreate(root));
            mouse.InjectMouseInput([new() { MouseOptions = InjectedInputMouseOptions.Move, DeltaX = 5, DeltaY = 12 }]);
            try
            {
                await AttachAsync(root, page);
                gate.SetResult([Item(1) with { Title = "漫长旅途中今天播出的第一集" }]);
                await page.PendingActivation;
                root.UpdateLayout();
                var broadcast = (ItemsRepeater)page.FindName("PersonalBroadcastList");
                var button = Find<Button>(broadcast, item => item.Tag is Anime { ID: 1 });
                var surface = Find<ContentPresenter>(button, item => item.Name == "Surface");
                Assert.Same(Application.Current.Resources["DesktopCardBrush"], surface.Background);
                Assert.Null(surface.BackgroundTransition);
                for (var i = 0; i < 4; i++)
                {
                    Assert.Same(Application.Current.Resources["DesktopCardBrush"], surface.Background);
                    await UnoVisualEvidence.CaptureAsync(page, "C-motion", $"today-{i:00}-enter");
                    await Task.Delay(16);
                }
                var target = button.TransformToVisual(root).TransformPoint(new Point(80, 28));
                mouse.InjectMouseInput([new() { MouseOptions = InjectedInputMouseOptions.Move, DeltaX = (int)target.X - 5, DeltaY = (int)target.Y - 12 }]);
                Assert.NotNull(surface.BackgroundTransition);
                Assert.Same(Application.Current.Resources["DesktopCardHoverBrush"], surface.Background);
                await Task.Delay(130);
                await UnoVisualEvidence.CaptureAsync(page, "C-motion", "today-04-actual-hover");
            }
            finally { gate.TrySetResult([]); await DetachAsync(root, page); }
        });

    [Theory]
    [InlineData(960, true, 7)]
    [InlineData(860, true, 7)]
    [InlineData(440, true, 4)]
    [InlineData(960, false, 7)]
    public Task TodayWeekUsesWholeRowsAndThemeSpecificTodayEdge(int width, bool paper, int firstRowCount)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = paper ? "Paper" : "Fluent11", ThemeSetting = "Dark", FontStyle = "Serif" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            await new TrackingService(fixture.Factory).SetStatusAsync(1, AnimeTrackingStatus.Watching);
            var today = fixture.Today(new Source { Schedule = (_, _) => Task.FromResult(
                new List<Anime> { Item(1) with { Title = "穿过很长的冬夜，今天继续踏上旅程" } }) });
            today.Width = width;
            try
            {
                await AttachAsync(root, today); await today.PendingActivation;
                root.UpdateLayout();
                var week = (ItemsRepeater)today.FindName("WeekStrip");
                var days = Enumerable.Range(0, 7).Select(i => Assert.IsType<Grid>(week.TryGetElement(i))).ToArray();
                var rows = days.GroupBy(day => Math.Round(day.TransformToVisual(week).TransformPoint(default).Y)).ToArray();
                Assert.Equal(firstRowCount, rows[0].Count());
                Assert.DoesNotContain(rows, row => row.Count() == 1);
                var highlight = Find<Border>(days[0], border => border.Name == "TodayHighlight");
                var edge = Assert.IsType<SolidColorBrush>(highlight.BorderBrush).Color;
                var accent = Assert.IsType<SolidColorBrush>(Application.Current.Resources["DesktopAccentBrush"]).Color;
                Assert.Equal(!paper, edge == accent);
                await UnoVisualEvidence.CaptureAsync(today, "C-motion", $"today-week-{(paper ? "Paper" : "Fluent11")}-{width}");
            }
            finally { await DetachAsync(root, today); }
        });

    private static async Task<ThemeService> PaperThemeAsync(Grid root, Fixture fixture)
    {
        var store = new DesktopSettingsStore(fixture);
        await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper", ThemeSetting = "Dark", FontStyle = "Serif" });
        var theme = new ThemeService(store);
        await theme.InitializeThemeAsync(root);
        return theme;
    }
}
