using AniMeido.App.Services;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopChoiceBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PaperSegmentsUseTheirOwnContainersPaddingAndSelectedSurface(bool dark)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper", ThemeSetting = dark ? "Dark" : "Light", FontStyle = "Serif" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var choice = new DesktopChoice { Width = 420, SelectedIndex = 1 };
            choice.Items.Add("喜欢"); choice.Items.Add("未设置"); choice.Items.Add("减少");
            root.Children.Add(choice);
            await Task.Delay(30);
            root.UpdateLayout();
            var segments = Assert.IsType<Grid>(choice.Content).Children.OfType<ListBox>().Single();
            Assert.Equal(Visibility.Visible, segments.Visibility);
            var selected = Assert.IsType<ListBoxItem>(segments.ContainerFromIndex(1));
            Assert.Same(Application.Current.Resources["DesktopSegmentItemStyle"], selected.Style);
            Assert.True(selected.Padding.Left >= 10 && selected.ActualWidth >= selected.MinWidth);
            Assert.True(selected.ActualHeight >= 32);
            var surface = FindSurface(selected);
            Assert.Equal(Assert.IsType<SolidColorBrush>(Application.Current.Resources["DesktopSegmentSelectedBrush"]).Color,
                Assert.IsType<SolidColorBrush>(surface.Background).Color);
            Assert.True(surface.CornerRadius.TopLeft > 0);
            segments.SelectedIndex = 2;
            Assert.Equal(2, choice.SelectedIndex);
        });

    private static Border FindSurface(DependencyObject node)
    {
        if (node is Border { Name: "SegmentSurface" } surface) return surface;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = TryFind(VisualTreeHelper.GetChild(node, index));
            if (child is not null) return child;
        }
        throw new InvalidOperationException("分段项缺少自己的 SegmentSurface。");
        Border? TryFind(DependencyObject child)
        {
            if (child is Border { Name: "SegmentSurface" } result) return result;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(child); index++)
                if (TryFind(VisualTreeHelper.GetChild(child, index)) is { } found) return found;
            return null;
        }
    }
}
