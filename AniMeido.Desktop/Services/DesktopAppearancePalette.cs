namespace AniMeido.App.Services;

public sealed record DesktopAppearancePalette(
    string Page, string Navigation, string Surface, string Card, string Field, string Overlay, string Text,
    string Muted, string Line, string Accent, string Selected)
{
    public static DesktopAppearancePalette For(DesktopVisualStyle style, bool dark) => (style, dark) switch
    {
        (DesktopVisualStyle.Fluent10, false) => new("#FFFFFF", "#E6E6E6", "#F5F5F5", "#FFFFFF", "#FFFFFF", "#F2F2F2",
            "#111111", "#5D5D5D", "#D9D9D9", "#0078D7", "#D8D8D8"),
        (DesktopVisualStyle.Fluent10, true) => new("#000000", "#1F1F1F", "#111111", "#1F1F1F", "#292929", "#2B2B2B",
            "#FFFFFF", "#B5B5B5", "#333333", "#0078D7", "#3D3D3D"),
        (DesktopVisualStyle.Paper, false) => new("#FAF9F5", "#F2F0E8", "#F5F4EF", "#FFFFFF", "#EFEDE6", "#FFFFFF",
            "#202020", "#626262", "#DEDBD2", "#B9553A", "#E5E5E5"),
        (DesktopVisualStyle.Paper, true) => new("#1F1F1F", "#191919", "#262624", "#30302E", "#343432", "#383835",
            "#EEEEEE", "#B5B5B5", "#41413D", "#D97757", "#444440"),
        (_, true) => new("#262626", "#202020", "#2D2D2D", "#353535", "#383838", "#3B3B3B",
            "#FFFFFF", "#B5B5B5", "#3A3A3A", "#60CDFF", "#454545"),
        _ => new("#F9F9F9", "#F3F3F3", "#FFFFFF", "#F5F5F5", "#F9F9F9", "#FFFFFF",
            "#1A1A1A", "#5D5D5D", "#E5E5E5", "#005FB8", "#E5E5E5"),
    };
}
