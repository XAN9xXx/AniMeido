using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace AniMeido.App.Controls;

public sealed class DesktopButtonSurfaceMotion : DependencyObject
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(DesktopButtonSurfaceMotion), new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);

    private static void OnEnableChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement surface || surface is not ContentPresenter and not Border) return;
        if (args.NewValue is true)
        {
            surface.PointerEntered += OnPointerEntered;
            surface.Unloaded += OnUnloaded;
        }
        else
        {
            surface.PointerEntered -= OnPointerEntered;
            surface.Unloaded -= OnUnloaded;
            SetTransition(surface, false);
        }
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        // 先画当前底色（包括选中态），避免切页时从模板默认刷子过渡出假悬停。
        SetTransition((FrameworkElement)sender,
            Application.Current.Resources.TryGetValue("DesktopMotionEnabled", out var enabled) && enabled is true);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args)
        => SetTransition((FrameworkElement)sender, false);

    private static void SetTransition(FrameworkElement surface, bool enabled)
    {
        if (surface is ContentPresenter content)
            content.BackgroundTransition = enabled ? content.BackgroundTransition ?? NewTransition() : null;
        else if (surface is Border border)
            border.BackgroundTransition = enabled ? border.BackgroundTransition ?? NewTransition() : null;

        static BrushTransition NewTransition() => new() { Duration = TimeSpan.FromMilliseconds(120) };
    }
}
