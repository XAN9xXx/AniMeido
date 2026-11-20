namespace AniMeido.Plugin.Base.ViewModels;

public static class DesktopRecommendationPreference
{
    public static int SelectionIndex(bool liked, bool reduced) => liked ? 0 : reduced ? 2 : 1;
}
