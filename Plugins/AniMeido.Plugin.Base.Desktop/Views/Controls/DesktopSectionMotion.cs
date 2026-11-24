using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace AniMeido.Plugin.Base.Views.Controls;

/// <summary>页内分区的短入场，只作用于专用正文容器，不参与数据加载或导航。</summary>
public sealed class DesktopSectionMotion
{
    private FrameworkElement? _host;
    private Storyboard? _slide;

    public void Play(FrameworkElement host, bool backward = false)
    {
        Stop();
        var resources = Application.Current.Resources;
        if (!host.IsLoaded || !resources.TryGetValue("DesktopMotionEnabled", out var enabled) || enabled is not true
            || !resources.TryGetValue("DesktopSectionMotionStyle", out var style)) return;

        var transform = new CompositeTransform();
        switch (style)
        {
            case "Fluent10": transform.ScaleX = transform.ScaleY = .975; break;
            case "Fluent11": transform.TranslateY = 14; break;
            case "Paper": transform.TranslateX = backward ? -18 : 18; break;
            default: return;
        }
        _host = host;
        var slide = _slide = new Storyboard();
        host.RenderTransformOrigin = new Point(.5, .5);
        host.RenderTransform = transform;
        // 替换的是同一块正文，新内容首帧就可读，底色不会先闪成空白。
        host.Opacity = style is "Paper" ? 1 : .78;
        Track(transform, nameof(transform.ScaleX), transform.ScaleX, 1);
        Track(transform, nameof(transform.ScaleY), transform.ScaleY, 1);
        Track(transform, nameof(transform.TranslateX), transform.TranslateX, 0);
        Track(transform, nameof(transform.TranslateY), transform.TranslateY, 0);
        Track(host, nameof(host.Opacity), host.Opacity, 1);
        host.Unloaded += OnUnloaded;
        DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged;
        slide.Completed += (_, _) => { if (ReferenceEquals(_slide, slide)) Stop(); };
        slide.Begin();

        void Track(DependencyObject element, string property, double from, double to)
        {
            if (from == to) return;
            var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(210),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, property);
            slide.Children.Add(animation);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => Stop();
    private void OnAppearanceChanged(object? sender, EventArgs args) => Stop();

    private void Stop()
    {
        if (_host is not { } host) return;
        _host = null;
        host.Unloaded -= OnUnloaded;
        DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;
        var slide = _slide;
        _slide = null;
        slide?.Stop();
        host.ClearValue(UIElement.RenderTransformProperty);
        host.ClearValue(UIElement.RenderTransformOriginProperty);
        host.ClearValue(UIElement.OpacityProperty);
    }
}
