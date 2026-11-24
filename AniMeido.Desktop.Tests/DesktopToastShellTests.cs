using System.Xml.Linq;
using AniMeido.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Xunit;
using static AniMeido.Desktop.Tests.UnoVisualEvidence;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopToastShellTests
{
    [Theory]
    [InlineData(760, 600, 1f)]
    [InlineData(455, 455, 1.25f)]
    public Task FunctionalToastsFitBelowTheActualShellTitleAndLeaveEmptyAreasClickable(int width, int height, float scale)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper", ThemeSetting = "Dark", FontStyle = "Serif" });
            await using var theme = new ThemeService(store);
            var shell = LoadShellGrid();
            shell.Width = width;
            shell.Height = height;
            shell.HorizontalAlignment = HorizontalAlignment.Left;
            shell.VerticalAlignment = VerticalAlignment.Top;
            root.Children.Add(shell);
            try
            {
                UnoVisualEvidence.SetScale(root.XamlRoot!, scale);
                await theme.InitializeThemeAsync(shell, Part<Grid>("FeedbackHost"));
                var body = Part<Grid>("ShellBody");
                var title = Part<Grid>("TitleBarHost");
                var frame = Part<Frame>("ContentFrame");
                Part<Grid>("StartupOverlay").Visibility = Visibility.Collapsed;
                var notice = Part<InfoBar>("LegacyInstallationInfoBar");
                notice.IsOpen = true;
                notice.Visibility = Visibility.Visible;
                var hits = 0;
                var probe = new Button
                {
                    Content = "空区点击", HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new(14, 0, 0, 0),
                };
                probe.Click += (_, _) => hits++;
                var blankHits = 0;
                var content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { probe } };
                content.PointerPressed += (_, e) => { if (ReferenceEquals(e.OriginalSource, content)) blankHits++; };
                frame.Content = content;
                // 和主窗口一样改动真实标题栏行高；校验提示跟随布局，不跟写死的像素。
                shell.RowDefinitions[0].Height = new(66);
                const string shortMessage = "档案已刷新";
                const string longMessage = "这是一条较长的功能提示。界面变窄时仍可以读完整句子，也可以用键盘关闭这一条提示。";
                const string errorMessage = "刷新失败，已保留上次的内容。";
                theme.ShowFeedback(shortMessage);
                var shortText = await ToastTextAsync(shortMessage);
                var shortToast = BorderOf(shortText);
                await UnoVisualEvidence.WaitForAsync(() => shortText.ActualWidth >= 65 && notice.ActualHeight > 80);
                var before = Bounds(shortToast);
                await UnoVisualEvidence.CaptureAsync(shell, "H-acceptance", $"toast-{width}-short");
                Assert.True(before.Top >= Bounds(body).Top, $"内容起点 {Bounds(body).Top:F1}；提示起点 {before.Top:F1}。");
                Assert.True(before.Top >= Bounds(title).Bottom);
                Assert.InRange(before.Height, 24, 39);
                Assert.InRange(before.Width, 80, 132);
                var mouse = new DesktopTestMouse(root);
                mouse.Click(Part<Button>("BackButton"));
                mouse.Click(probe);
                Assert.Equal(1, hits);

                theme.ShowFeedback(longMessage);
                theme.ShowFeedback(errorMessage, error: true);
                var longText = await ToastTextAsync(longMessage);
                var longToast = BorderOf(longText);
                var error = BorderOf(await ToastTextAsync(errorMessage));
                await UnoVisualEvidence.CaptureAsync(shell, "H-acceptance", $"toast-{width}-stack");
                Assert.True(longText.ActualHeight > shortText.ActualHeight);
                Assert.True(Bounds(longToast).Right <= Bounds(body).Right);
                Assert.True(Bounds(shortToast).Top >= Bounds(longToast).Bottom);
                Assert.Equal(before.Width, shortToast.ActualWidth, 1);
                Assert.True(Bounds(error).Top >= Bounds(shortToast).Bottom);
                Assert.True(Bounds(error).Bottom <= Bounds(body).Bottom);
                mouse.Click(probe);
                Assert.Equal(2, hits);
                var blankX = Math.Max(Bounds(frame).Left, Bounds(longToast).Left) + 20;
                Assert.True(blankX < Bounds(shortToast).Left);
                mouse.Click(shell, blankX, Bounds(shortToast).Top + 8);
                Assert.Equal(1, blankHits);
                var close = Descendants(shortToast).OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "关闭提示");
                Assert.True(close.Focus(FocusState.Keyboard));
                mouse.Key(VirtualKey.Space);
                await UnoVisualEvidence.WaitForAsync(() => !shortToast.IsLoaded);
                Assert.True(longToast.IsLoaded && error.IsLoaded);
                if (scale > 1)
                {
                    theme.ShowFeedback("第一条追加提示");
                    theme.ShowFeedback("第二条追加提示");
                    theme.ShowFeedback("第三条追加提示");
                    await ToastTextAsync("第三条追加提示");
                    var scrolling = Descendants(Part<Grid>("FeedbackHost")).OfType<ScrollView>()
                        .Single(view => view.VerticalAlignment == VerticalAlignment.Top);
                    await UnoVisualEvidence.WaitForAsync(() => scrolling.ScrollableHeight > 30);
                    var oldestClose = Descendants(longToast).OfType<Button>().Single();
                    Assert.True(oldestClose.Focus(FocusState.Keyboard));
                    await UnoVisualEvidence.WaitForAsync(() => scrolling.VerticalOffset > 0);
                    mouse.Key(VirtualKey.Space);
                    await UnoVisualEvidence.WaitForAsync(() => !longToast.IsLoaded);
                    Assert.True(error.IsLoaded);
                }

                T Part<T>(string name) where T : FrameworkElement => Assert.IsAssignableFrom<T>(shell.FindName(name));
                Rect Bounds(FrameworkElement item)
                {
                    var pt = item.TransformToVisual(shell).TransformPoint(default);
                    return new(pt.X, pt.Y, item.ActualWidth, item.ActualHeight);
                }
                async Task<TextBlock> ToastTextAsync(string value)
                {
                    TextBlock? text = null;
                    await UnoVisualEvidence.WaitForAsync(() => (text = Descendants(shell).OfType<TextBlock>()
                        .FirstOrDefault(item => item.IsLoaded && item.ActualHeight > 0 && item.Text == value)) is not null);
                    return text!;
                }
            }
            finally
            {
                root.Children.Remove(shell);
                UnoVisualEvidence.SetScale(root.XamlRoot!, 0);
            }
        });

    private static Border BorderOf(TextBlock text)
    {
        for (var current = VisualTreeHelper.GetParent(text); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Border border) return border;
        throw new InvalidOperationException("找不到实际提示边框。");
    }

    private static Grid LoadShellGrid()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
            directory = directory.Parent ?? throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
        var window = XDocument.Load(Path.Combine(directory.FullName, "AniMeido.Desktop", "MainWindow.xaml")).Root!;
        var ns = window.Name.Namespace;
        var grid = new XElement(window.Element(ns + "Grid")!);
        // 使用实际外壳的布局和控件，只摘掉宿主事件与导航模板资源。
        grid.Element(ns + "Grid.Resources")?.Remove();
        var events = new[] { "Loaded", "SizeChanged", "KeyDown", "Click", "Closed" };
        foreach (var attribute in grid.DescendantsAndSelf().Attributes().Where(attr => events.Contains(attr.Name.LocalName)).ToArray())
            attribute.Remove();
        var shell = Assert.IsType<Grid>(XamlReader.Load(grid.ToString()));
        // 指针命中也受工作区裁剪影响，补上刚摘掉的主窗口尺寸处理。
        var workspace = (Border)shell.FindName("Workspace");
        workspace.SizeChanged += (_, e) => ((RectangleGeometry)workspace.Clip).Rect =
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        return shell;
    }
}
