using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AniMeido.Plugin.Base.Views.Controls;

public sealed class DesktopDisclosureChevron : UserControl
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(DesktopDisclosureChevron),
        new PropertyMetadata(false, (sender, _) => ((DesktopDisclosureChevron)sender).UpdateAngle(animate: true)));
    public static readonly DependencyProperty FlyoutProperty = DependencyProperty.Register(
        nameof(Flyout), typeof(FlyoutBase), typeof(DesktopDisclosureChevron), new PropertyMetadata(null,
            (sender, _) => ((DesktopDisclosureChevron)sender).ConnectFlyout()));

    private readonly RotateTransform _rotation = new();
    private Storyboard? _animation;
    private FlyoutBase? _connectedFlyout;
    private bool _wasPaper;

    public DesktopDisclosureChevron()
    {
        IsTabStop = false;
        IsHitTestVisible = false;
        FontSize = 12;
        var icon = new FontIcon
        {
            Glyph = "\uE70D", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            RenderTransformOrigin = new(0.5, 0.5), RenderTransform = _rotation,
        };
        icon.SetBinding(IconElement.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = this, Path = new PropertyPath(nameof(Foreground)),
        });
        icon.SetBinding(FontIcon.FontSizeProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = this, Path = new PropertyPath(nameof(FontSize)),
        });
        Content = icon;
        Loaded += (_, _) =>
        {
            DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged;
            ConnectFlyout();
        };
        Unloaded += (_, _) =>
        {
            DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;
            SetFlyout(null);
            _animation?.Stop();
            _animation = null;
        };
    }

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public FlyoutBase? Flyout { get => (FlyoutBase?)GetValue(FlyoutProperty); set => SetValue(FlyoutProperty, value); }
    public bool PaperOnly { get; set; }

    private void ConnectFlyout()
    {
        if (!IsLoaded) return;
        SetFlyout(Flyout);
        UpdateAngle(animate: false);
    }

    private void SetFlyout(FlyoutBase? flyout)
    {
        if (_connectedFlyout == flyout) return;
        if (_connectedFlyout is { } previous)
        {
            previous.Opened -= OnFlyoutChanged;
            previous.Closed -= OnFlyoutChanged;
        }
        _connectedFlyout = flyout;
        if (flyout is not null)
        {
            flyout.Opened += OnFlyoutChanged;
            flyout.Closed += OnFlyoutChanged;
        }
    }

    private void OnFlyoutChanged(object? sender, object args) => UpdateAngle(animate: true);

    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        var resources = Application.Current.Resources;
        if ((resources["DesktopUseSegmentedChoices"] is true) != _wasPaper || resources["DesktopMotionEnabled"] is not true)
            UpdateAngle(animate: false);
    }

    private void UpdateAngle(bool animate)
    {
        var resources = Application.Current.Resources;
        var paper = resources.TryGetValue("DesktopUseSegmentedChoices", out var style) && style is true;
        _wasPaper = paper;
        var target = (_connectedFlyout?.IsOpen ?? IsOpen) && (!PaperOnly || paper) ? 180d : 0d;
        var current = _rotation.Angle;
        _animation?.Stop();
        _animation = null;
        if (!animate || !IsLoaded || !paper || Math.Abs(current - target) < 0.1
            || !resources.TryGetValue("DesktopMotionEnabled", out var motion) || motion is not true)
        {
            _rotation.Angle = target;
            return;
        }

        // 计时器首帧尚未来时仍画起点，避免先闪到终点再开始旋转。
        _rotation.Angle = current;
        var flip = new DoubleAnimation
        {
            From = current, To = target, Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(flip, _rotation);
        Storyboard.SetTargetProperty(flip, nameof(RotateTransform.Angle));
        var animation = _animation = new Storyboard { Children = { flip } };
        animation.Completed += (_, _) =>
        {
            if (_animation != animation) return;
            _rotation.Angle = target;
            _animation = null;
            animation.Stop();
        };
        animation.Begin();
    }
}
