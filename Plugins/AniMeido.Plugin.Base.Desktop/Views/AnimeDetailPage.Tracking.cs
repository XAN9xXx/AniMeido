using AniMeido.Contracts.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class AnimeDetailPage
{
    private string DesktopTrackingLabel(AnimeTrackingStatus status)
        => ViewModel.TrackingActions.FirstOrDefault(action => action.Status == status)?.ActiveLabel
            ?? "未标记";

    private void OnDesktopTrackingMenuOpening(object sender, object args)
    {
        DesktopTrackingMenu.Items.Clear();
        var current = ViewModel.CurrentStatus;
        var accent = (Brush)Application.Current.Resources["DesktopAccentBrush"];
        // 作品换季后，当前标记仍需可见；其他不适用的操作继续隐藏。
        foreach (var action in ViewModel.TrackingActions.Where(action => action.IsVisible || action.Status == current))
        {
            var isCurrent = action.Status == current;
            var item = new TrackingMenuItem(accent)
            {
                Text = action.Label,
                IsChecked = isCurrent,
            };
            item.Click += (_, _) =>
            {
                if (isCurrent) item.IsChecked = true;
                else _ = ChangeTrackingAsync(action.Status);
            };
            DesktopTrackingMenu.Items.Add(item);
        }

        if (ViewModel.HasCurrentStatus)
        {
            DesktopTrackingMenu.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem { Text = "取消标记" };
            remove.Click += (_, _) => _ = ChangeTrackingAsync(AnimeTrackingStatus.None);
            DesktopTrackingMenu.Items.Add(remove);
        }
    }

    private void OnDesktopTrackingUnloaded(object sender, RoutedEventArgs args)
        => DesktopTrackingMenu.Hide();

    private sealed class TrackingMenuItem(Brush accent) : ToggleMenuFlyoutItem
    {
        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (GetTemplateChild("CheckGlyph") is FontIcon check)
                check.Foreground = accent;
        }
    }
}
