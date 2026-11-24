using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AniMeido.Plugin.Base.Views.Controls;

public interface IDesktopLoadingPage
{
    DesktopPageLoadSurface LoadSurface { get; }
}

public sealed class DesktopPageLoadSurface
{
    private enum State { Hidden, Loading, Error }

    private readonly FrameworkElement _body;
    private readonly FrameworkElement _placeholder;
    private readonly ProgressRing? _ring;
    private readonly bool _acceptsInput;
    private State _state;
    private Storyboard? _fade;
    private int _revision;

    public DesktopPageLoadSurface(FrameworkElement body, FrameworkElement placeholder, ProgressRing? ring = null)
    {
        _body = body;
        _placeholder = placeholder;
        _ring = ring;
        _acceptsInput = placeholder.IsHitTestVisible;
        Settle();
    }

    public FrameworkElement? ReadyTarget { get; private set; }
    public event EventHandler? Ready;

    public void Present(FrameworkElement? target = null)
    {
        var first = ReadyTarget is null;
        ReadyTarget = target ?? _body;
        // 刷新可把空结果换成列表。下次返回用新的可见区域，本次不重复入场。
        if (first) Ready?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        ReadyTarget = null;
        Settle();
    }

    public void Unload()
    {
        _state = State.Hidden;
        Settle();
    }

    public void SetPlaceholder(bool visible, bool error = false)
    {
        var next = !visible ? State.Hidden : error ? State.Error : State.Loading;
        if (next == _state) return;
        var shown = _placeholder.Visibility == Visibility.Visible;
        var from = shown ? _placeholder.Opacity : 0;
        _state = next;
        StopFade();
        _placeholder.IsHitTestVisible = visible && (_acceptsInput || error);
        var resources = Application.Current.Resources;
        if (error || !shown && !visible
            || !resources.TryGetValue("DesktopFluentPageMotionEnabled", out var motion) || motion is not true)
        {
            Settle();
            return;
        }

        DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged;
        if (visible && !shown)
        {
            // 同一轮内命中缓存时直接显示正文，不先闪出一个加载环。
            var revision = _revision;
            if (!_placeholder.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (revision == _revision) Fade(0, 1);
            })) Settle();
        }
        else Fade(from, visible ? 1 : 0);
    }

    private void Fade(double from, double to)
    {
        if (to == 1) SetRing(true);
        _placeholder.Visibility = Visibility.Visible;
        _placeholder.Opacity = from;
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var fade = new Storyboard();
        Storyboard.SetTarget(animation, _placeholder);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        fade.Children.Add(animation);
        _fade = fade;
        fade.Completed += (_, _) => { if (ReferenceEquals(_fade, fade)) Settle(); };
        fade.Begin();
    }

    private void OnAppearanceChanged(object? sender, EventArgs args) => Settle();

    private void StopFade()
    {
        ++_revision;
        DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;
        var fade = _fade;
        _fade = null;
        fade?.Stop();
    }

    private void Settle()
    {
        StopFade();
        _placeholder.Visibility = _state == State.Hidden ? Visibility.Collapsed : Visibility.Visible;
        _placeholder.Opacity = 1;
        _placeholder.IsHitTestVisible = _state == State.Error || _state == State.Loading && _acceptsInput;
        SetRing(_state == State.Loading);
    }

    private void SetRing(bool active)
    {
        if (_ring is null) return;
        _ring.IsActive = active;
        _ring.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }
}
