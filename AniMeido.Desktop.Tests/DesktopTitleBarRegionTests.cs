using AniMeido.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopTitleBarRegionTests
{
    [Theory]
    [InlineData(1, 8, 6, 40, 36)]
    [InlineData(1.25, 10, 7, 50, 46)]
    [InlineData(1.5, 12, 9, 60, 54)]
    [InlineData(2, 16, 12, 80, 72)]
    public void PassthroughUsesPhysicalPixelsAndRoundsOutward(double scale, int x, int y, int width, int height)
        => Assert.Equal(new RectInt32(x, y, width, height), DesktopTitleBarRegions.ToPhysicalBounds(new Rect(8, 6, 40, 36), scale));

    [Fact]
    public Task TheLoadedBackButtonPublishesItsBoundsAndTracksThemeSizeChanges()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var button = new Button { Width = 40, Height = 36, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 6, 0, 0) };
            RectInt32[] published = [];
            using var regions = new DesktopTitleBarRegions(root, button, value => published = value);
            root.Children.Add(button);
            root.UpdateLayout();
            await Task.Delay(20);
            Assert.Equal(new RectInt32(8, 6, 40, 36), Assert.Single(published));
            button.Width = 48; button.Height = 32; button.Margin = new Thickness(0);
            root.UpdateLayout();
            await Task.Delay(20);
            Assert.Equal(new RectInt32(0, 0, 48, 32), Assert.Single(published));
        });
}
