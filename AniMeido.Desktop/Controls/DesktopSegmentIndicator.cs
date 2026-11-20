using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AniMeido.App.Controls;

public sealed class DesktopSegmentIndicator : Canvas
{
    public static readonly DependencyProperty OwnerProperty = DependencyProperty.Register(
        nameof(Owner), typeof(ListBox), typeof(DesktopSegmentIndicator),
        new PropertyMetadata(null, (sender, _) => ((DesktopSegmentIndicator)sender).Connect()));
    public static readonly DependencyProperty DisplayedSlotProperty = DependencyProperty.Register(
        nameof(DisplayedSlot), typeof(double), typeof(DesktopSegmentIndicator),
        new PropertyMetadata(0d, (sender, _) => ((DesktopSegmentIndicator)sender).UpdateOffset()));

    private readonly TranslateTransform _translation = new();
    private readonly Border _pill;
    private ListBox? _connectedOwner;
    private Storyboard? _slide;
    private bool _positioned;
    private double _slotWidth;
    private double _itemLeftMargin;

    public DesktopSegmentIndicator()
    {
        IsHitTestVisible = false;
        _pill = new Border { Name = "SegmentSelection", IsHitTestVisible = false,
            BorderThickness = new Thickness(1), RenderTransform = _translation };
        Children.Add(_pill);
        Loaded += (_, _) =>
        {
            DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged;
            Connect();
        };
        Unloaded += (_, _) =>
        {
            DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;
            SetOwner(null);
            CancelSlide();
            _positioned = false;
        };
        SizeChanged += (_, _) => UpdateGeometry();
    }

    public ListBox? Owner { get => (ListBox?)GetValue(OwnerProperty); set => SetValue(OwnerProperty, value); }
    public double DisplayedSlot { get => (double)GetValue(DisplayedSlotProperty); set => SetValue(DisplayedSlotProperty, value); }

    private void Connect()
    {
        if (!IsLoaded) return;
        SetOwner(Owner);
        RefreshAppearance();
        PositionPill(animate: false);
    }

    private void SetOwner(ListBox? owner)
    {
        if (_connectedOwner is { } previous)
        {
            previous.SelectionChanged -= OnSelectionChanged;
            previous.IsEnabledChanged -= OnEnabledChanged;
        }
        _connectedOwner = owner;
        if (owner is not null)
        {
            owner.SelectionChanged += OnSelectionChanged;
            owner.IsEnabledChanged += OnEnabledChanged;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
        => PositionPill(animate: true);

    private void OnEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
        => RefreshEnabled();

    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        RefreshAppearance();
        if (!CanAnimate()) PositionPill(animate: false);
    }

    private void RefreshAppearance()
    {
        var resources = Application.Current.Resources;
        _pill.Background = (Brush)resources["DesktopSegmentSelectedBrush"];
        _pill.BorderBrush = (Brush)resources["DesktopSegmentSelectedEdgeBrush"];
        _pill.CornerRadius = (CornerRadius)resources["DesktopSegmentItemRadius"];
        RefreshEnabled();
    }

    private void RefreshEnabled() => _pill.Opacity = _connectedOwner?.IsEnabled == false ? .45 : 1;

    private void PositionPill(bool animate)
    {
        var previous = _positioned;
        UpdateGeometry();
        if (_connectedOwner is not { Visibility: Visibility.Visible, SelectedIndex: >= 0 } owner)
        {
            CancelSlide();
            _positioned = false;
            return;
        }

        var target = (double)owner.SelectedIndex;
        var current = DisplayedSlot;
        CancelSlide();
        if (animate && previous && CanAnimate() && Math.Abs(target - current) > .01)
        {
            DisplayedSlot = current;
            var movement = new DoubleAnimation
            {
                From = current, To = target, Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(movement, this);
            Storyboard.SetTargetProperty(movement, nameof(DisplayedSlot));
            var slide = _slide = new Storyboard { Children = { movement } };
            slide.Completed += (_, _) =>
            {
                if (_slide != slide) return;
                DisplayedSlot = target;
                CancelSlide();
            };
            slide.Begin();
        }
        else DisplayedSlot = target;
        _positioned = true;
    }

    private void UpdateGeometry()
    {
        if (_connectedOwner is not { Visibility: Visibility.Visible, SelectedIndex: >= 0 } owner
            || owner.SelectedItem is not ListBoxItem item || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _pill.Visibility = Visibility.Collapsed;
            return;
        }

        // 按段位置插值。字体变化导致轨道改宽时，画面仍在同一次切换里继续移动。
        _slotWidth = ActualWidth / owner.Items.Count;
        _itemLeftMargin = item.Margin.Left;
        _pill.Width = Math.Max(0, _slotWidth - item.Margin.Left - item.Margin.Right);
        _pill.Height = Math.Max(0, ActualHeight - item.Margin.Top - item.Margin.Bottom);
        SetTop(_pill, item.Margin.Top);
        _pill.Visibility = Visibility.Visible;
        UpdateOffset();
    }

    private void UpdateOffset() => _translation.X = DisplayedSlot * _slotWidth + _itemLeftMargin;

    private static bool CanAnimate()
    {
        var resources = Application.Current.Resources;
        return resources["DesktopUseSegmentedChoices"] is true && resources["DesktopMotionEnabled"] is true;
    }

    private void CancelSlide()
    {
        _slide?.Stop();
        _slide = null;
    }
}
