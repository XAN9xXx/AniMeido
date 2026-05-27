namespace AniMeido.Plugin.Base.Models
{
    public enum DragAction
    {
        None = 0,
        Watching = 1,
        PlanToWatch = 2,
        NotInterested = 3,
    }

    public enum DragPosition
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    public class DragZoneConfig
    {
        public DragPosition Position { get; set; }
        public DragAction Action { get; set; }
        public double SizePercent { get; set; } = 0.25;

        public static List<DragZoneConfig> GetDefaults() => new()
        {
            new() { Position = DragPosition.TopLeft, Action = DragAction.NotInterested, SizePercent = 0.25 },
            new() { Position = DragPosition.TopRight, Action = DragAction.Watching, SizePercent = 0.25 },
            new() { Position = DragPosition.BottomLeft, Action = DragAction.PlanToWatch, SizePercent = 0.25 },
            new() { Position = DragPosition.BottomRight, Action = DragAction.None, SizePercent = 0.25 },
        };
    }
}
