using AniMeido.Contracts.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class AnimeDetailPage
{
    private string DesktopTrackingLabel(AnimeTrackingStatus status)
        => ViewModel.TrackingActions.FirstOrDefault(action => action.Status == status)?.ActiveLabel
            ?? "未标记";

    private void OnDesktopTrackingMenuOpening(object sender, object args)
    {
        DesktopTrackingMenu.Items.Clear();
        foreach (var action in ViewModel.VisibleTrackingActions)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = action.Label,
                Icon = new FontIcon { Glyph = action.Glyph },
                IsChecked = action.Status == ViewModel.CurrentStatus,
            };
            item.Click += (_, _) => _ = ChangeTrackingAsync(action.Status);
            DesktopTrackingMenu.Items.Add(item);
        }

        if (ViewModel.HasCurrentStatus)
        {
            DesktopTrackingMenu.Items.Add(new MenuFlyoutSeparator());
            var status = ViewModel.CurrentStatus;
            var remove = new MenuFlyoutItem { Text = "取消标记" };
            remove.Click += (_, _) => _ = ChangeTrackingAsync(status);
            DesktopTrackingMenu.Items.Add(remove);
        }
    }

    private void OnDesktopTrackingUnloaded(object sender, RoutedEventArgs args)
        => DesktopTrackingMenu.Hide();
}
