namespace AniMeido.Plugin.Base.Views;

// 只供 Uno 窗口把手动 F5 交给正在显示的页面。
public interface IDesktopManualRefresh
{
    bool TryRefreshFromKeyboard(Action<string> showSuccess);
}
