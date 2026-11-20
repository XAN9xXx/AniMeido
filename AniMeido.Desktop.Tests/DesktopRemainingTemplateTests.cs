using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopRemainingTemplateTests
{
    public static TheoryData<DesktopVisualStyle, bool> Modes => DesktopInteractionRenderTests.Modes;
    [Fact]
    public Task SelectionControlsUseOwnedTemplateRoots()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var combo = new ComboBox { Header = "排序", ItemsSource = new[] { "时间", "评分" }, SelectedIndex = 0 };
            root.Children.Add(combo); root.UpdateLayout(); await Task.Delay(20);
            Assert.NotNull(Find<Grid>(combo, element => element.Name == "DesktopComboRoot"));
        });

    [Fact]
    public Task EmptyComboShowsItsPlaceholderAgainAfterClearingTheSelection()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var combo = new ComboBox { PlaceholderText = "选择关联作品", ItemsSource = new[] { "作品1", "作品2" }, Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            root.Children.Add(combo); root.UpdateLayout(); await Task.Delay(20);
            var presenter = Find<ContentPresenter>(combo, p => p.Name == "ContentPresenter");
            var placeholder = Assert.IsType<TextBlock>(presenter.Content);
            Assert.Equal("选择关联作品", placeholder.Text);
            Assert.Equal(Visibility.Visible, placeholder.Visibility);
            Assert.True(placeholder.ActualWidth > 0);
            combo.SelectedIndex = 0; Assert.Equal("作品1", presenter.Content);
            combo.SelectedIndex = -1; root.UpdateLayout();
            Assert.Same(placeholder, presenter.Content); Assert.Equal(Visibility.Visible, placeholder.Visibility);
        });

    [Theory]
    [MemberData(nameof(Modes))]
    public Task ComboKeepsHeaderSelectionPopupAndFocusStates(DesktopVisualStyle visual, bool dark)
        => DesktopInteractionRenderTests.WithThemeAsync(visual, dark, async root =>
        {
            var combo = new ComboBox { Header = "排序", Description = "选择排序方式", ItemsSource = new[] { "时间", "评分", "人数" },
                SelectedIndex = 0, Width = 220, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            root.Children.Add(combo); root.UpdateLayout(); await Task.Delay(20);
            Assert.Equal("排序", Find<ContentPresenter>(combo, p => p.Name == "HeaderContentPresenter").Content);
            Assert.Equal("时间", Find<ContentPresenter>(combo, p => p.Name == "ContentPresenter").Content);
            Assert.True(VisualStateManager.GoToState(combo, "Focused", false));
            var surface = Find<Border>(combo, p => p.Name == "ComboSurface");
            Assert.Equal(ColorOf("DesktopFieldFocusBrush"), ((SolidColorBrush)surface.BorderBrush).Color);
            combo.IsDropDownOpen = true; await Task.Delay(20); root.UpdateLayout();
            var container = Assert.IsType<ComboBoxItem>(combo.ContainerFromIndex(1));
            Assert.Same(Application.Current.Resources[visual == DesktopVisualStyle.Paper ? "DesktopPaperComboBoxItemStyle" : "DesktopNativeComboBoxItemStyle"], container.Style);
            combo.SelectedIndex = 1; combo.IsDropDownOpen = false;
            Assert.Equal("评分", Find<ContentPresenter>(combo, p => p.Name == "ContentPresenter").Content);
            combo.IsEnabled = false;
            Assert.Equal(0.4, Find<Grid>(combo, p => p.Name == "DesktopComboRoot").Opacity);
            combo.IsEnabled = true;
            Assert.Equal(1, Find<Grid>(combo, p => p.Name == "DesktopComboRoot").Opacity);
        });

    [Theory]
    [MemberData(nameof(Modes))]
    public Task SwitchKeepsHeaderAndToggleAutomationWithOwnedTrack(DesktopVisualStyle visual, bool dark)
        => DesktopInteractionRenderTests.WithThemeAsync(visual, dark, async root =>
        {
            var toggle = new ToggleSwitch { Header = "启用提醒", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            root.Children.Add(toggle); root.UpdateLayout(); await Task.Delay(20);
            Assert.Equal("启用提醒", Find<ContentPresenter>(toggle, p => p.Name == "HeaderContentPresenter").Content);
            var provider = Assert.IsAssignableFrom<IToggleProvider>(new ToggleSwitchAutomationPeer(toggle).GetPattern(PatternInterface.Toggle));
            provider.Toggle(); Assert.True(toggle.IsOn);
            await Task.Delay(180);
            Assert.Equal(20, Find<Grid>(toggle, p => p.Name == "SwitchKnob").RenderTransform is TranslateTransform transform ? transform.X : -1);
            provider.Toggle(); Assert.False(toggle.IsOn);
            toggle.IsEnabled = false;
            Assert.Equal(0.4, Find<Grid>(toggle, p => p.Name == "DesktopSwitchRoot").Opacity);
            toggle.IsEnabled = true; Assert.Equal(1, Find<Grid>(toggle, p => p.Name == "DesktopSwitchRoot").Opacity);
        });

    [Theory]
    [MemberData(nameof(Modes))]
    public Task ExpanderHeaderRemainsKeyboardToggleAndOwnsItsContent(DesktopVisualStyle visual, bool dark)
        => DesktopInteractionRenderTests.WithThemeAsync(visual, dark, async root =>
        {
            var expander = new Expander { Header = "更多筛选", Content = new TextBox { Text = "保留输入" }, Width = 300,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            root.Children.Add(expander); root.UpdateLayout(); await Task.Delay(20);
            var header = Find<ToggleButton>(expander, p => p.Name == "ExpanderHeader");
            Assert.Equal(Visibility.Collapsed, Find<Border>(expander, p => p.Name == "ExpanderContentClip").Visibility);
            header.IsChecked = true; Assert.True(expander.IsExpanded);
            Assert.Equal(Visibility.Visible, Find<Border>(expander, p => p.Name == "ExpanderContentClip").Visibility);
            header.IsChecked = false; Assert.False(expander.IsExpanded);
            Assert.Equal("保留输入", ((TextBox)expander.Content).Text);
            Assert.True(header.UseSystemFocusVisuals);
        });

    [Theory]
    [MemberData(nameof(Modes))]
    public Task ListContainersKeepSelectionDuringHoverAndDisable(DesktopVisualStyle visual, bool dark)
        => DesktopInteractionRenderTests.WithThemeAsync(visual, dark, async root =>
        {
            var list = new ListView { ItemsSource = new[] { new TextBlock { Text = "甲" }, new TextBlock { Text = "乙" } },
                SelectionMode = ListViewSelectionMode.Multiple, Width = 300, Height = 180,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            root.Children.Add(list); root.UpdateLayout(); await Task.Delay(20);
            var item = Assert.IsType<ListViewItem>(list.ContainerFromIndex(0));
            item.IsSelected = true;
            var surface = Find<Border>(item, p => p.Name == "DesktopListSurface");
            Assert.Equal(ColorOf("DesktopListSelectedBrush"), ((SolidColorBrush)surface.Background).Color);
            Assert.True(VisualStateManager.GoToState(item, "SelectedPointerOver", false));
            Assert.True(item.IsSelected);
            item.IsEnabled = false; Assert.Equal(0.4, surface.Opacity);
            item.IsEnabled = true; Assert.True(item.IsSelected); Assert.Equal(1, surface.Opacity);
        });

    [Theory]
    [MemberData(nameof(Modes))]
    public Task DialogOwnsButtonsAndDeferralCanKeepTheEditorOpen(DesktopVisualStyle visual, bool dark)
        => DesktopInteractionRenderTests.WithThemeAsync(visual, dark, async root =>
        {
            var editor = new TextBox { Text = "保留编辑内容" };
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "保存", Content = editor,
                PrimaryButtonText = "保存", SecondaryButtonText = "另存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            var save = false;
            dialog.PrimaryButtonClick += (_, args) => { var deferral = args.GetDeferral(); args.Cancel = !save; deferral.Complete(); };
            var result = dialog.ShowAsync().AsTask();
            try
            {
                await Task.Delay(30); root.UpdateLayout();
                Assert.NotNull(Find<Grid>(dialog, p => p.Name == "LayoutRoot"));
                var primary = Find<Button>(dialog, b => b.Name == "PrimaryButton");
                Assert.Equal("保存", primary.Content);
                Assert.Equal("另存", Find<Button>(dialog, b => b.Name == "SecondaryButton").Content);
                Assert.Equal("取消", Find<Button>(dialog, b => b.Name == "CloseButton").Content);
                Invoke(primary); await Task.Delay(20); Assert.False(result.IsCompleted); Assert.Equal("保留编辑内容", editor.Text);
                save = true; Invoke(primary);
                Assert.Equal(ContentDialogResult.Primary, await result);
            }
            finally { dialog.Hide(); await result; }
        });

    private static Color ColorOf(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    [Theory]
    [MemberData(nameof(Modes))]
    public Task OwnedControlGalleryRendersAllThemes(DesktopVisualStyle visual, bool dark)
        => DesktopInteractionRenderTests.WithThemeAsync(visual, dark, async root =>
        {
            var panel = new StackPanel { Spacing = 20, Padding = new(32), Width = 620, HorizontalAlignment = HorizontalAlignment.Left,
                Background = (Brush)Application.Current.Resources["DesktopPageBrush"] };
            panel.Children.Add(new TextBlock { Text = "界面控件", Style = (Style)Application.Current.Resources["AniMeidoPageTitleStyle"] });
            panel.Children.Add(new ComboBox { Header = "排序方式", ItemsSource = new[] { "放送时间", "综合评分", "评分人数" }, SelectedIndex = 1, Width = 260 });
            panel.Children.Add(new ToggleSwitch { Header = "启用截图音效", IsOn = true, HorizontalAlignment = HorizontalAlignment.Left });
            panel.Children.Add(new Expander { Header = "更多筛选", IsExpanded = true, Content = new TextBox { Header = "年份", Text = "2026" } });
            panel.Children.Add(new ListView { Height = 128, ItemsSource = new[] { "星海中的远行", "风吹过夏天", "山间的小小旅馆" }, SelectedIndex = 1 });
            root.Children.Add(panel); root.UpdateLayout(); await Task.Delay(180);
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(panel);
            Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
            var pixels = (await bitmap.GetPixelsAsync()).ToArray(); Assert.Contains(pixels, p => p != 0);
            if (Environment.GetEnvironmentVariable("ANIMEIDO_TEST_CAPTURE_DIR") is not { Length: > 0 } directory) return;
            Directory.CreateDirectory(directory);
            using var image = SKImage.FromPixelCopy(new SKImageInfo(bitmap.PixelWidth, bitmap.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul), pixels);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(directory, $"controls-{visual}-{dark}.png")); png.SaveTo(file);
        });
}
