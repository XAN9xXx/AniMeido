namespace AniMeido.App.Services;

public sealed record DesktopAppearanceButtonColors(
    string Primary,
    string OnPrimary,
    string Hover,
    string Pressed,
    string ControlEdge,
    string ControlBottomEdge,
    string PrimaryEdge,
    string PrimaryBottomEdge)
{
    public static DesktopAppearanceButtonColors For(DesktopVisualStyle visual, bool dark)
        => visual switch
        {
            DesktopVisualStyle.Paper => dark
                ? new("#D97757", "#1F1F1F", "#DF8263", "#D37153", "#41413D", "#41413D", "#D97757", "#D97757")
                : new("#B9553A", "#FFFFFF", "#AA4D34", "#97432D", "#DEDBD2", "#DEDBD2", "#B9553A", "#B9553A"),
            DesktopVisualStyle.Fluent10 => new("#0078D7", "#FFFFFF", dark ? "#1084E0" : "#006CBE", "#005A9E",
                "#D9D9D9", "#D9D9D9", "#0078D7", "#0078D7"),
            _ => dark
                ? new("#60CDFF", "#001F33", "#56BCEB", "#4CAAD6", "#454545", "#292929", "#60CDFF", "#3BA8DB")
                : new("#005FB8", "#FFFFFF", "#196EBF", "#317CC4", "#E5E5E5", "#CCCCCC", "#00539F", "#003E75"),
        };
}
