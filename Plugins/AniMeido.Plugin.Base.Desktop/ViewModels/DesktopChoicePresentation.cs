namespace AniMeido.Plugin.Base.ViewModels;

internal static class DesktopChoicePresentation
{
    internal static bool UseSegments(bool paper, int optionCount)
        => paper && optionCount is 2 or 3;
}
