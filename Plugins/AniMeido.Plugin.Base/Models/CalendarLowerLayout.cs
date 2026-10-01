namespace AniMeido.Plugin.Base.Models
{
    /// <summary>下方两块的有限布局：先缩小内容，再由页面滚动承载最低可用高度。</summary>
    internal readonly record struct CalendarLowerLayout(
        bool StackPanels,
        bool CompactTimeMachineHeader,
        bool StackTimeMachineActions,
        double Height,
        double DailyPickHeight,
        double DiscoverHeight,
        double DiscoverWidth,
        double DiscoverChromeHeight,
        int Rows,
        int Columns)
    {
        internal const double PanelSpacing = 14;
        internal const double DailyPickWidth = 480;
        internal const double DailyPickChromeHeight = 68;
        internal const double DailyPickCoverMinHeight = 96;
        internal const double DailyPickCoverMaxHeight = 168;
        internal const double DiscoverChromeWidth = 34;
        internal const double PickMinWidth = 260;
        internal const double PickMinHeight = 64;
        internal const double PickMaxHeight = 96;
        internal const double PickRowSpacing = 8;
        private const double PickColumnSpacing = 10;
        // 并排时为时光机的年份按钮与跳转入口留出宽度，而不是隐藏今日一抽。
        private const double DiscoverMinSideWidth = 360;
        private const double TimeMachineHeaderMinWidth = 600;

        internal static CalendarLowerLayout Calculate(double width, double availableHeight, bool hasDailyPick)
        {
            var stackPanels = hasDailyPick && width < DailyPickWidth + PanelSpacing + DiscoverMinSideWidth;
            var discoverWidth = hasDailyPick && !stackPanels
                ? width - DailyPickWidth - PanelSpacing
                : width;
            var compactHeader = discoverWidth < TimeMachineHeaderMinWidth;
            var stackActions = discoverWidth < DiscoverMinSideWidth;
            // 标题换行增加 28 + 8；很窄时两组操作再分行，增加 28 + 4。
            var discoverChromeHeight = 68 + (compactHeader ? 36 : 0) + (stackActions ? 32 : 0);
            var discoverMinHeight = discoverChromeHeight + PickMinHeight;
            var dailyPickMinHeight = DailyPickChromeHeight + DailyPickCoverMinHeight;
            var dailyPickMaxHeight = DailyPickChromeHeight + DailyPickCoverMaxHeight;
            double height;
            double dailyPickHeight;
            double discoverHeight;

            if (stackPanels)
            {
                dailyPickHeight = Math.Clamp(
                    availableHeight - PanelSpacing - discoverMinHeight,
                    dailyPickMinHeight,
                    dailyPickMaxHeight);
                discoverHeight = Math.Max(discoverMinHeight, availableHeight - dailyPickHeight - PanelSpacing);
                height = dailyPickHeight + PanelSpacing + discoverHeight;
            }
            else
            {
                height = Math.Max(availableHeight, discoverMinHeight);
                if (hasDailyPick)
                    height = Math.Max(height, dailyPickMinHeight);
                dailyPickHeight = hasDailyPick ? Math.Min(height, dailyPickMaxHeight) : 0;
                discoverHeight = height;
            }

            var rows = Math.Max(1, (int)Math.Floor(
                (discoverHeight - discoverChromeHeight + PickRowSpacing) / (PickMinHeight + PickRowSpacing)));
            var columns = Math.Clamp((int)Math.Floor(
                (discoverWidth - DiscoverChromeWidth + PickColumnSpacing) / (PickMinWidth + PickColumnSpacing)), 1, 3);

            return new CalendarLowerLayout(stackPanels, compactHeader, stackActions, height,
                dailyPickHeight, discoverHeight, discoverWidth, discoverChromeHeight, rows, columns);
        }
    }
}
