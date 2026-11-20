namespace AniMeido.App.Services;

public enum DesktopLoadingAnimation { FiveDots, Arc, ClaudeNodes, ClaudeOrbit, ClaudeBreath, ClaudeQuiet, Gemini }

internal static class DesktopLoadingSelection
{
    internal static DesktopLoadingAnimation Choose(int family, int claudeVariant)
        => family switch
        {
            0 => DesktopLoadingAnimation.FiveDots,
            1 => DesktopLoadingAnimation.Arc,
            2 => claudeVariant switch
            {
                0 => DesktopLoadingAnimation.ClaudeNodes,
                1 => DesktopLoadingAnimation.ClaudeOrbit,
                2 => DesktopLoadingAnimation.ClaudeBreath,
                3 => DesktopLoadingAnimation.ClaudeQuiet,
                _ => throw new ArgumentOutOfRangeException(nameof(claudeVariant)),
            },
            3 => DesktopLoadingAnimation.Gemini,
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

    internal static DesktopLoadingAnimation RandomAnimation() => Choose(Random.Shared.Next(4), Random.Shared.Next(4));

}

internal sealed class DesktopLoadingSession(Func<DesktopLoadingAnimation> choose)
{
    internal DesktopLoadingAnimation Animation { get; private set; }
    private bool _active;

    internal void Update(bool active)
    {
        if (active && !_active)
            Animation = choose();
        _active = active;
    }
}
