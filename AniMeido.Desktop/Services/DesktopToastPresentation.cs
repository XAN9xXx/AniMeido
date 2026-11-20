namespace AniMeido.App.Services;

internal static class DesktopToastLayout
{
    internal const double OuterMargin = 24;
    internal const double StackGap = 12;

    internal static double StackMaximumHeight(double windowHeight)
        => Math.Max(0, (windowHeight - OuterMargin * 2 - StackGap) / 2);
}

internal sealed class DesktopToastLifetime(TimeSpan duration, TimeProvider clock)
{
    private TimeSpan _remaining = duration;
    private long _startedTimestamp = clock.GetTimestamp();
    private bool _pointerOver;
    private bool _focusInside;

    internal bool IsPaused => _pointerOver || _focusInside;

    internal TimeSpan Remaining
    {
        get
        {
            var remaining = IsPaused ? _remaining : _remaining - clock.GetElapsedTime(_startedTimestamp);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    internal void SetPointerOver(bool value) => SetInteraction(value, _focusInside);

    internal void SetFocusInside(bool value) => SetInteraction(_pointerOver, value);

    private void SetInteraction(bool pointerOver, bool focusInside)
    {
        var remaining = Remaining;
        var wasPaused = IsPaused;
        _pointerOver = pointerOver;
        _focusInside = focusInside;
        if (wasPaused == IsPaused)
            return;

        _remaining = remaining;
        if (!IsPaused)
            _startedTimestamp = clock.GetTimestamp();
    }
}
