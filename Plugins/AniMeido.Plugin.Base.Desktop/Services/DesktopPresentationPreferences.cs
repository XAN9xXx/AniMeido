namespace AniMeido.Plugin.Base.Services;

/// <summary>桌面页面的展示选择；只在界面线程更新。</summary>
public static class DesktopPresentationPreferences
{
    public static bool UseDetailedCatalogCards { get; private set; }
    public static bool ShowPlanReminders { get; private set; }
    public static event EventHandler? Changed;
    public static event EventHandler? AppearanceChanged;

    public static void NotifyAppearanceChanged() => AppearanceChanged?.Invoke(null, EventArgs.Empty);

    public static void Apply(bool useDetailedCatalogCards, bool showPlanReminders)
    {
        if (UseDetailedCatalogCards == useDetailedCatalogCards
            && ShowPlanReminders == showPlanReminders)
            return;
        UseDetailedCatalogCards = useDetailedCatalogCards;
        ShowPlanReminders = showPlanReminders;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
