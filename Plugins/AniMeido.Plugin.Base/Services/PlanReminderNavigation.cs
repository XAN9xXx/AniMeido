using AniMeido.Contracts;
using AniMeido.Plugin.Base.Views;

namespace AniMeido.Plugin.Base.Services;

internal sealed class PlanReminderNavigation(IPluginNavigator navigator) : IPlanReminderNavigation
{
    public void OpenPlan(int animeId) => navigator.Navigate(typeof(TodayPage), animeId);
}
