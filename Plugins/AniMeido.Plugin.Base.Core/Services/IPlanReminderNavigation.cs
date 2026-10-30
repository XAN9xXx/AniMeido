namespace AniMeido.Plugin.Base.Services;

/// <summary>打开提醒对应的补番计划；具体页面由桌面插件选择。</summary>
public interface IPlanReminderNavigation
{
    void OpenPlan(int animeId);
}
