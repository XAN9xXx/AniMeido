namespace AniMeido.Plugin.Base.ViewModels;

internal readonly record struct DesktopCalendarLayout(
    bool StackPanels, double DailyPickWidth, double DiscoverWidth, int Columns)
{
    internal const double PanelSpacing = 22;
    internal const double PanelHeight = 272;
    internal int Capacity => Columns * 2;

    internal static double TimeMachineMinimumItemWidth(double panelWidth, int columns)
        // Native Fill counts one spacing after every item when deciding how many columns fit.
        => Math.Max(0, Math.Floor((panelWidth - 34) / columns) - 10);

    internal static DesktopCalendarLayout Calculate(double width, bool hasDailyPick)
    {
        var stacked = hasDailyPick && width < 860;
        var dailyWidth = !hasDailyPick ? 0 : stacked ? width : Math.Min(440, (width - PanelSpacing) * 0.4);
        var discoverWidth = hasDailyPick && !stacked ? width - dailyWidth - PanelSpacing : width;
        return new(stacked, dailyWidth, discoverWidth, discoverWidth >= 490 ? 2 : 1);
    }
}
