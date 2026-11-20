using AniMeido.App.Services;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopToastPresentationTests
{
    [Fact]
    public void PointerPauseResumesOnlyTheRemainingLifetime()
    {
        var clock = new ManualClock();
        var lifetime = new DesktopToastLifetime(TimeSpan.FromSeconds(4), clock);
        clock.Advance(TimeSpan.FromSeconds(1.5));
        lifetime.SetPointerOver(true);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(2.5), lifetime.Remaining);

        lifetime.SetPointerOver(false);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(0.5), lifetime.Remaining);
    }

    [Fact]
    public void FocusKeepsTheCountdownPausedAfterThePointerLeaves()
    {
        var clock = new ManualClock();
        var lifetime = new DesktopToastLifetime(TimeSpan.FromSeconds(4), clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        lifetime.SetPointerOver(true);
        lifetime.SetFocusInside(true);
        lifetime.SetPointerOver(false);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(lifetime.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(3), lifetime.Remaining);

        lifetime.SetFocusInside(false);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.Zero, lifetime.Remaining);
    }

    [Fact]
    public void RepeatedInteractionEventsNeverRestartTheLifetime()
    {
        var clock = new ManualClock();
        var lifetime = new DesktopToastLifetime(TimeSpan.FromSeconds(4), clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        lifetime.SetFocusInside(true);
        clock.Advance(TimeSpan.FromSeconds(1));
        lifetime.SetFocusInside(true);
        lifetime.SetFocusInside(false);
        clock.Advance(TimeSpan.FromSeconds(2));
        lifetime.SetFocusInside(false);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.Zero, lifetime.Remaining);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(200, 70)]
    [InlineData(800, 370)]
    [InlineData(1060, 500)]
    public void BothStacksFitTheWindowWithTheirOuterMarginAndGap(double windowHeight, double expected)
    {
        var maximum = DesktopToastLayout.StackMaximumHeight(windowHeight);
        Assert.Equal(expected, maximum);
        if (windowHeight >= 60)
            Assert.True(maximum * 2 + 60 <= windowHeight);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
