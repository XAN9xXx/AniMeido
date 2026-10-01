using AniMeido.Plugin.Base.Models;

namespace AniMeido.Tests;

/// <summary>放送日历下方布局的尺寸策略；不实例化 WinUI 页面。</summary>
public sealed class CurrentSeasonLayoutTests
{
    [Theory]
    [InlineData(-200)]
    [InlineData(0)]
    [InlineData(120)]
    public void ShortViewport_KeepsBothPanelsAndOneTimeMachineRow(double availableHeight)
    {
        var layout = CalendarLowerLayout.Calculate(1100, availableHeight, hasDailyPick: true);

        Assert.False(layout.StackPanels);
        Assert.True(layout.Height > availableHeight);
        Assert.Equal(164, layout.DailyPickHeight);
        Assert.True(layout.DiscoverHeight >= layout.DiscoverChromeHeight + 64);
        Assert.Equal(1, layout.Rows);
    }

    [Theory]
    [InlineData(240, 2)]
    [InlineData(400, 4)]
    public void AdequateViewport_UsesRemainingHeightAndCapsDailyPick(double availableHeight, int expectedRows)
    {
        var layout = CalendarLowerLayout.Calculate(1800, availableHeight, hasDailyPick: true);

        Assert.False(layout.CompactTimeMachineHeader);
        Assert.Equal(availableHeight, layout.Height);
        Assert.Equal(236, layout.DailyPickHeight);
        Assert.Equal(expectedRows, layout.Rows);
        Assert.Equal(3, layout.Columns);
    }

    [Theory]
    [InlineData(500, 346, false)]
    [InlineData(300, 378, true)]
    public void NarrowViewport_StacksPanelsAndRetainsHeaderActions(double width, double expectedHeight, bool stackActions)
    {
        var layout = CalendarLowerLayout.Calculate(width, 100, hasDailyPick: true);

        Assert.True(layout.StackPanels);
        Assert.True(layout.CompactTimeMachineHeader);
        Assert.Equal(stackActions, layout.StackTimeMachineActions);
        Assert.Equal(expectedHeight, layout.Height);
        Assert.Equal(164, layout.DailyPickHeight);
        Assert.Equal(layout.DailyPickHeight + 14 + layout.DiscoverHeight, layout.Height);
        Assert.Equal(1, layout.Rows);
        Assert.Equal(1, layout.Columns);
    }

    [Fact]
    public void NarrowTallViewport_UsesRemainingHeightAfterDailyPick()
    {
        var layout = CalendarLowerLayout.Calculate(500, 600, hasDailyPick: true);

        Assert.True(layout.StackPanels);
        Assert.Equal(600, layout.Height);
        Assert.Equal(236, layout.DailyPickHeight);
        Assert.Equal(350, layout.DiscoverHeight);
        Assert.Equal(3, layout.Rows);
    }

    [Theory]
    [InlineData(853, true)]
    [InlineData(854, false)]
    public void SideBySideBreakpoint_DoesNotHideDailyPick(double width, bool stackPanels)
    {
        var layout = CalendarLowerLayout.Calculate(width, 400, hasDailyPick: true);

        Assert.Equal(stackPanels, layout.StackPanels);
        Assert.True(layout.DailyPickHeight >= 164);
        Assert.True(layout.DiscoverWidth >= 360);
    }

    [Theory]
    [InlineData(300, 200)]
    [InlineData(500, 168)]
    [InlineData(1000, 132)]
    public void NoDailyPick_UsesFullWidthWithoutReservingEmptySpace(double width, double expectedHeight)
    {
        var layout = CalendarLowerLayout.Calculate(width, 0, hasDailyPick: false);

        Assert.False(layout.StackPanels);
        Assert.Equal(0, layout.DailyPickHeight);
        Assert.Equal(width, layout.DiscoverWidth);
        Assert.Equal(expectedHeight, layout.Height);
        Assert.Equal(1, layout.Rows);
        Assert.InRange(layout.Columns, 1, 3);
    }

    [Theory]
    [InlineData(1093, true)]
    [InlineData(1094, false)]
    public void HeaderWrapping_UsesTimeMachineWidthNotPageWidth(double width, bool compactHeader)
    {
        var layout = CalendarLowerLayout.Calculate(width, 400, hasDailyPick: true);

        Assert.False(layout.StackPanels);
        Assert.Equal(compactHeader, layout.CompactTimeMachineHeader);
    }
}
