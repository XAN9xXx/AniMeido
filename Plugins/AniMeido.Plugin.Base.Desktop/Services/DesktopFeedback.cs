namespace AniMeido.Plugin.Base.Services;

/// <summary>桌面外壳接管的瞬时操作反馈；在界面线程发布和订阅。</summary>
public static class DesktopFeedback
{
    public static event Action<string, bool>? Requested;

    public static bool TryShow(string message, bool error = false)
    {
        var requested = Requested;
        if (requested is null)
            return false;

        requested(message, error);
        return true;
    }
}
