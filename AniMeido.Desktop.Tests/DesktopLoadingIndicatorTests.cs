using AniMeido.App.Controls;
using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopLoadingIndicatorTests
{
    [Fact]
    public Task LoadingColorMotionAndReattachmentKeepOneSelectionUntilTheNextOperation()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var choices = 0;
            var brush = new SolidColorBrush(Colors.Orange);
            var indicator = new DesktopLoadingIndicator(() => { choices++; return DesktopLoadingAnimation.ClaudeNodes; })
            {
                Width = 32, Height = 32, Foreground = brush, IsActive = true,
            };
            Assert.False(indicator.HasFrameSubscription);
            await AttachAsync(root, indicator);
            Assert.True(indicator.HasFrameSubscription);
            Assert.Equal(1, choices);
            Application.Current.Resources["DesktopMotionEnabled"] = false;
            DesktopPresentationPreferences.NotifyAppearanceChanged();
            Assert.False(indicator.HasFrameSubscription);
            Assert.Equal(1, choices);
            Application.Current.Resources["DesktopMotionEnabled"] = true;
            DesktopPresentationPreferences.NotifyAppearanceChanged();
            Assert.True(indicator.HasFrameSubscription);
            Assert.Equal(Colors.Orange, indicator.RenderedColor);

            brush.Color = Colors.Blue;
            Assert.Equal(Colors.Blue, indicator.RenderedColor);
            Assert.Equal(1, choices);
            indicator.MotionEnabled = false;
            Assert.False(indicator.HasFrameSubscription);
            indicator.MotionEnabled = true;
            Assert.True(indicator.HasFrameSubscription);

            await DetachAsync(root, indicator);
            Assert.False(indicator.HasFrameSubscription);
            brush.Color = Colors.Red;
            await AttachAsync(root, indicator);
            Assert.Equal(Colors.Red, indicator.RenderedColor);
            Assert.Equal(1, choices);
            indicator.IsActive = false;
            Assert.False(indicator.HasFrameSubscription);
            indicator.IsActive = true;
            Assert.Equal(2, choices);
            await DetachAsync(root, indicator);
        });

    [Fact]
    public Task FrameUpdatesKeepXamlMeasurementsUnchanged()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            Application.Current.Resources["DesktopMotionEnabled"] = false;
            var indicator = new DesktopLoadingIndicator(() => DesktopLoadingAnimation.ClaudeNodes)
            {
                Width = 32, Height = 32, IsActive = true, MotionEnabled = false,
            };
            await AttachAsync(root, indicator);
            root.UpdateLayout();
            var measures = indicator.MeasureCount;
            for (var frame = 0; frame < 120; frame++)
                indicator.DrawFrame(frame / 60d, still: false);
            root.UpdateLayout();
            Assert.Equal(measures, indicator.MeasureCount);
            Assert.False(indicator.HasFrameSubscription);
            await DetachAsync(root, indicator);
        });

    private static async Task AttachAsync(Grid root, FrameworkElement element)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Loaded(object sender, RoutedEventArgs args) => ready.TrySetResult();
        element.Loaded += Loaded;
        try
        {
            root.Children.Add(element);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { element.Loaded -= Loaded; }
    }

    private static async Task DetachAsync(Grid root, FrameworkElement element)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Unloaded(object sender, RoutedEventArgs args) => ready.TrySetResult();
        element.Unloaded += Unloaded;
        try
        {
            root.Children.Remove(element);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { element.Unloaded -= Unloaded; }
    }
}
