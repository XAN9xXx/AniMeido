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
            var surface = UnoVisualEvidence.Descendants(segments).OfType<Border>().Single(border => border.Name == "SegmentSelection");
            Assert.Equal(Assert.IsType<SolidColorBrush>(Application.Current.Resources["DesktopSegmentSelectedBrush"]).Color,
                Assert.IsType<SolidColorBrush>(surface.Background).Color);
            Assert.True(surface.CornerRadius.TopLeft > 0);
            Assert.InRange(Math.Abs(surface.TransformToVisual(segments).TransformPoint(default).X
                - selected.TransformToVisual(segments).TransformPoint(default).X), 0, 1);
            segments.SelectedIndex = 2;
            Assert.Equal(2, choice.SelectedIndex);
        });
}
