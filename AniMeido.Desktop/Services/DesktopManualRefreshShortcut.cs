using AniMeido.Plugin.Base.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace AniMeido.App.Services;

internal static class DesktopManualRefreshShortcut
{
    internal static void Handle(Frame frame, KeyRoutedEventArgs args, Action<string> showSuccess)
    {
        if (args.Handled || args.Key != VirtualKey.F5 || args.KeyStatus.WasKeyDown
            || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift) || IsDown(VirtualKey.Menu)
            || frame.Content is not Page { IsLoaded: true } active || active is not IDesktopManualRefresh refresh
            || frame.XamlRoot is not { } root
            || VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.IsOpen))
            return;

        args.Handled = refresh.TryRefreshFromKeyboard(showSuccess);
    }

    private static bool IsDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
