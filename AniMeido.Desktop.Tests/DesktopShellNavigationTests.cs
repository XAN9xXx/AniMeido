using AniMeido.App.Controls;
using AniMeido.App.Services;
using AniMeido.App.Themes;
using AniMeido.App;
using AniMeido.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopShellNavigationTests
{
    [Fact]
    public void StartupPrefersTodayOverTheFirstMenuItem()
    {
        var first = PluginNavigationItem.CreatePage("日历", "", "Calendar", typeof(Page));
        var today = PluginNavigationItem.CreatePage("今天", "", "AniMeido.Plugin.Base.Views.TodayPage", typeof(Page));
        Assert.Same(today, MainWindow.SelectInitialPage([first, today]));
    }

    [Fact]
    public void StartupSkipsSettingsAndUnresolvedPages()
    {
        var settings = PluginNavigationItem.CreatePage("插件设置", "", "Settings", typeof(Page), isSettingsPage: true);
        var missing = PluginNavigationItem.CreatePage("缺失页面", "", "Missing");
        var first = PluginNavigationItem.CreatePage("日历", "", "Calendar", typeof(Page));
        Assert.Same(first, MainWindow.SelectInitialPage([settings, missing, first]));
        Assert.Null(MainWindow.SelectInitialPage([settings, missing]));
    }

    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10, 4, 0)]
    [InlineData(DesktopVisualStyle.Fluent11, 3, 4)]
    [InlineData(DesktopVisualStyle.Paper, 0, 6)]
    public Task SelectedNavigationUsesItsOwnCompleteSurface(DesktopVisualStyle visual, double indicatorWidth, double radius)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            root.Resources.MergedDictionaries.Add(new DesktopShellResources());
            var item = new DesktopShellNavigationItem("放送日历", "\uE787")
            {
                Style = (Style)root.Resources["DesktopShellNavigationStyle"],
                Width = 190, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, IsChecked = true,
            };
            item.ApplyAppearance(visual, expanded: true, Application.Current.Resources);
            root.Children.Add(item);
            await Task.Delay(30);
            root.UpdateLayout();

            Assert.Equal(indicatorWidth, item.SelectionIndicatorWidth);
            Assert.Equal(radius, item.CornerRadius.TopLeft);
            Assert.True(item.IsChecked);
            Assert.True(item.ActualHeight >= item.MinHeight);
            Assert.True(item.CaptionFits);
            Assert.Equal("放送日历", item.Caption);

            item.ApplyAppearance(visual, expanded: false, Application.Current.Resources);
            root.UpdateLayout();
            Assert.False(item.CaptionVisible);
            Assert.True(item.IsChecked);
        });

    [Fact]
    public Task LargerFontsIncreaseNavigationHeightInsteadOfClippingTheSelection()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            root.Resources.MergedDictionaries.Add(new DesktopShellResources());
            Application.Current.Resources["DesktopControlTextSize"] = 32d;
            var item = new DesktopShellNavigationItem("设置", "\uE713")
            {
                Style = (Style)root.Resources["DesktopShellNavigationStyle"], Width = 190,
                VerticalAlignment = VerticalAlignment.Top, IsChecked = true,
            };
            item.ApplyAppearance(DesktopVisualStyle.Paper, expanded: true, Application.Current.Resources);
            root.Children.Add(item);
            await Task.Delay(30);
            root.UpdateLayout();
            Assert.True(double.IsNaN(item.Height));
            Assert.True(item.ActualHeight > 44);
            Assert.True(item.CaptionFits);
            Assert.Equal(0, item.SelectionIndicatorWidth);
        });
}
