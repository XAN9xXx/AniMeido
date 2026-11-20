using AniMeido.Plugin.Base.ViewModels;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopCalendarLayoutTests
{
    [Theory]
    [InlineData(634, 2)]
    [InlineData(634.4, 2)]
    [InlineData(490, 2)]
    [InlineData(650, 2)]
    [InlineData(430, 1)]
    [InlineData(350, 1)]
    public void TimeMachineMinimumWidthKeepsTheRequestedColumnsAfterNativeFill(
        double panelWidth, int columns)
    {
        const double paddingAndBorder = 34;
        const double spacing = 10;
        var available = (float)(panelWidth - paddingAndBorder);
        var minimum = DesktopCalendarLayout.TimeMachineMinimumItemWidth(panelWidth, columns);

        // UniformGridLayout counts the final spacing while choosing its Fill column count.
        var nativeColumns = Math.Min(columns, Math.Max(1, (int)(available / (minimum + spacing))));
        Assert.Equal(columns, nativeColumns);
        var used = nativeColumns * (minimum + spacing) - spacing;
        var extra = (int)(available - used) / nativeColumns;
        var finalWidth = minimum + extra;
        Assert.InRange(available - (finalWidth * nativeColumns + spacing * (nativeColumns - 1)), 0, columns);
        Assert.InRange(finalWidth, (available - spacing * (columns - 1)) / columns - 1,
            (available - spacing * (columns - 1)) / columns);
    }

    [Fact]
    public void WideCalendarKeepsTheDailyPickAndFourTimeMachineCards()
    {
        var layout = DesktopCalendarLayout.Calculate(1034, hasDailyPick: true);

        Assert.False(layout.StackPanels);
        Assert.InRange(layout.DailyPickWidth, 390, 430);
        Assert.Equal(2, layout.Columns);
        Assert.Equal(4, layout.Capacity);
        Assert.True(DesktopCalendarLayout.PanelHeight >= 260);
    }

    [Fact]
    public void NarrowCalendarStacksBothSectionsWithoutDroppingTheDailyPick()
    {
        var layout = DesktopCalendarLayout.Calculate(650, hasDailyPick: true);

        Assert.True(layout.StackPanels);
        Assert.Equal(650, layout.DailyPickWidth);
        Assert.Equal(650, layout.DiscoverWidth);
        Assert.Equal(4, layout.Capacity);
    }

    [Fact]
    public void VeryNarrowCalendarKeepsTwoReadableTimeMachineRows()
    {
        var layout = DesktopCalendarLayout.Calculate(430, hasDailyPick: true);

        Assert.Equal(1, layout.Columns);
        Assert.Equal(2, layout.Capacity);
    }

    [Fact]
    public void MissingDailyPickGivesItsSpaceToTheTimeMachine()
    {
        var layout = DesktopCalendarLayout.Calculate(1034, hasDailyPick: false);

        Assert.False(layout.StackPanels);
        Assert.Equal(0, layout.DailyPickWidth);
        Assert.Equal(1034, layout.DiscoverWidth);
        Assert.Equal(4, layout.Capacity);
    }
}
