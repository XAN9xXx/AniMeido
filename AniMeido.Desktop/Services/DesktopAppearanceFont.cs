namespace AniMeido.App.Services;

public sealed record DesktopAppearanceFont(
    string Family,
    string ReadingFamily,
    double TitleSize,
    ushort TitleWeight,
    double SectionSize,
    ushort SectionWeight,
    double ControlSize,
    double DataSize,
    double BodySize,
    double BodyLineHeight)
{
    private const string ClassicFamily = "Segoe UI";
    private const string ModernFamily = "Segoe UI Variable Text";
    private const string SerifFamily = "ms-appx:///Assets/Fonts/DesktopSerif.ttf#Desktop Serif";

    private static readonly DesktopAppearanceFont Classic = new(
        ClassicFamily, ClassicFamily, 32, 300, 20, 400, 14, 13, 15, 1.75);
    private static readonly DesktopAppearanceFont Modern = new(
        ModernFamily, ModernFamily, 28, 600, 15, 600, 14, 13, 15, 1.72);
    private static readonly DesktopAppearanceFont Serif = new(
        SerifFamily, SerifFamily, 34, 400, 17, 400, 16, 14, 17, 1.8);

    public static DesktopAppearanceFont ForStyle(DesktopFontStyle style)
        => style switch
        {
            DesktopFontStyle.Classic => Classic,
            DesktopFontStyle.System => Modern,
            DesktopFontStyle.Serif => Serif,
            _ => throw new ArgumentOutOfRangeException(nameof(style)),
        };

    public static DesktopFontStyle FromChoiceIndex(int index)
        => index switch
        {
            0 => DesktopFontStyle.Classic,
            1 => DesktopFontStyle.System,
            2 => DesktopFontStyle.Serif,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    public static int ToChoiceIndex(DesktopFontStyle style)
        => style switch
        {
            DesktopFontStyle.Classic => 0,
            DesktopFontStyle.System => 1,
            DesktopFontStyle.Serif => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(style)),
        };
}
