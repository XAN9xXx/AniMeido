using AniMeido.App.Services;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopPageLoadSurfaceTests
{
    [Fact]
    public Task CacheHitSkipsTheRingAndErrorImmediatelyAcceptsRetry()
        => WithSurfaceAsync(async (surface, overlay, ring, _) =>
        {
            var starts = 0;
            var token = ring.RegisterPropertyChangedCallback(ProgressRing.IsActiveProperty,
                (_, _) => { if (ring.IsActive) starts++; });
            try
            {
                surface.SetPlaceholder(true);
                surface.SetPlaceholder(false);
                surface.Present();
                await AfterQueuedShowsAsync(overlay);
                Assert.Equal(0, starts);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);

                surface.Reset();
                surface.SetPlaceholder(true);
                surface.SetPlaceholder(true, error: true);
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                Assert.Equal(1, overlay.Opacity);
                Assert.True(overlay.IsHitTestVisible);
                Assert.False(ring.IsActive);
                await AfterQueuedShowsAsync(overlay);
                Assert.Equal(0, starts);
                Assert.True(overlay.IsHitTestVisible);
            }
            finally { ring.UnregisterPropertyChangedCallback(ProgressRing.IsActiveProperty, token); }
        });

    [Fact]
    public Task ReducingMotionDuringFadeOutFinishesItAndPreservesPassiveLoading()
        => WithSurfaceAsync(async (surface, overlay, ring, reduceMotion) =>
        {
            surface.SetPlaceholder(true);
            await UnoVisualEvidence.WaitForAsync(() => ring.IsActive);
            Assert.False(overlay.IsHitTestVisible);
            surface.SetPlaceholder(false);
            Assert.True(ring.IsActive);
            Assert.False(overlay.IsHitTestVisible);

            reduceMotion();
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.False(ring.IsActive);
            surface.SetPlaceholder(true);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Equal(1, overlay.Opacity);
            Assert.True(ring.IsActive);
            Assert.False(overlay.IsHitTestVisible);
        });

    private static Task WithSurfaceAsync(Func<DesktopPageLoadSurface, Grid, ProgressRing, Action, Task> check)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var enabled = true;
            await using var theme = new ThemeService(new DesktopSettingsStore(fixture), null, () => enabled);
            await theme.InitializeThemeAsync(root);
            var body = new Border();
            var ring = new ProgressRing();
            var overlay = new Grid { IsHitTestVisible = false, Children = { ring } };
            root.Children.Add(body);
            root.Children.Add(overlay);
            var surface = new DesktopPageLoadSurface(body, overlay, ring);
            try { await check(surface, overlay, ring, () => { enabled = false; theme.RefreshSystemMotionPreference(); }); }
            finally { surface.Unload(); }
        });

    private static Task AfterQueuedShowsAsync(FrameworkElement element)
    {
        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => turn.SetResult()));
        return turn.Task;
    }
}
