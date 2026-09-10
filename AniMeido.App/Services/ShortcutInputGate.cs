namespace AniMeido.App.Services;

internal sealed class ShortcutInputGate
{
    private int _keyDown;
    private int _actionRunning;

    public bool TryBegin()
        => Interlocked.Exchange(ref _keyDown, 1) == 0
            && Interlocked.CompareExchange(
                ref _actionRunning,
                1,
                0) == 0;

    public void ReleaseKey()
        => Interlocked.Exchange(ref _keyDown, 0);

    public void CompleteAction()
        => Interlocked.Exchange(ref _actionRunning, 0);
}
