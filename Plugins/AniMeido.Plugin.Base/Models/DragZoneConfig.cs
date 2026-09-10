namespace AniMeido.Plugin.Base.Models
{
    public enum DragAction
    {
        None = 0,
        Watching = 1,
        PlanToWatch = 2,
        NotInterested = 3,
        Following = 4,
        Completed = 5,
        Dropped = 6,
        Blocked = 7,
    }

    public class DragZoneConfig
    {
        /// <summary>唯一标识，用于区分各区域。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        /// <summary>显示名称，如"左上角""新区域"。</summary>
        public string Label { get; set; } = "新区域";

        /// <summary>距左侧位置百分比 (0.0 ~ 1.0)。</summary>
        public double XPercent { get; set; }

        /// <summary>距顶部位置百分比 (0.0 ~ 1.0)。</summary>
        public double YPercent { get; set; }

        /// <summary>宽度占比 (0.0 ~ 1.0)。</summary>
        public double WidthPercent { get; set; } = 0.25;

        /// <summary>高度占比 (0.0 ~ 1.0)。</summary>
        public double HeightPercent { get; set; } = 0.25;

        public DragAction Action { get; set; } = DragAction.None;

        public static List<DragZoneConfig> GetDefaults() => new()
        {
            new() { Id = "tl", Label = "左上角", XPercent = 0.0, YPercent = 0.0, WidthPercent = 0.25, HeightPercent = 0.25, Action = DragAction.NotInterested },
            new() { Id = "tr", Label = "右上角", XPercent = 0.75, YPercent = 0.0, WidthPercent = 0.25, HeightPercent = 0.25, Action = DragAction.Watching },
            new() { Id = "bl", Label = "左下角", XPercent = 0.0, YPercent = 0.75, WidthPercent = 0.25, HeightPercent = 0.25, Action = DragAction.PlanToWatch },
            new() { Id = "br", Label = "右下角", XPercent = 0.75, YPercent = 0.75, WidthPercent = 0.25, HeightPercent = 0.25, Action = DragAction.None },
        };
    }
}
