using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AniMeido.Plugin.Base.Views.Controls;

public sealed class DesktopWheelShelf : ScrollViewer
{
    private static readonly ConditionalWeakTable<Page, WheelSequence> Sequences = new();
    private Page? _page;
    private WheelSequence? _sequence;
    private double? _wheelTarget;

    public DesktopWheelShelf()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Uno 的 presenter 在套用模板时注册滚轮处理。先注册这一层，让改向的刻度只被处理一次。
        PointerWheelChanged += OnWheel;
        PointerPressed += (_, _) => _wheelTarget = null;
        ViewChanged += (_, args) => { if (!args.IsIntermediate) _wheelTarget = null; };
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_page is not null) return;
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (IsPopupBoundary(parent)) return;
            if (parent is not Page page) continue;
            _page = page;
            _sequence = Sequences.GetValue(page, static owner => new(owner));
            _sequence.ShelfCount++;
            return;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _wheelTarget = null;
        if (_page is not { } page || _sequence is not { } sequence) return;
        _page = null;
        _sequence = null;
        sequence.Forget(this);
        if (--sequence.ShelfCount != 0) return;
        sequence.Detach(page);
        Sequences.Remove(page);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        var point = ReadPlainVerticalWheel(args);
        if (point is null)
        {
            _wheelTarget = null;
            return;
        }
        if (_sequence is not { } sequence || HorizontalScrollMode == ScrollMode.Disabled
            || HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled || ScrollableWidth <= 0
            || !IsShelfInput(args.OriginalSource as DependencyObject)
            || !sequence.ContinuesOn(this, point.Timestamp))
            return;

        // 公众偏移量是已经滚到的位置。连续刻度和边缘判断用尚未完成的目标。
        var from = Math.Clamp(_wheelTarget ?? HorizontalOffset, 0, ScrollableWidth);
        var target = Math.Clamp(from - point.Properties.MouseWheelDelta, 0, ScrollableWidth);
        if (target == from) return;

        _wheelTarget = target;
        if (ChangeView(target, null, null, disableAnimation: false))
            args.Handled = true;
        else
            _wheelTarget = null;
    }

    private bool IsShelfInput(DependencyObject? source)
    {
        for (var element = source; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, this)) return true;
            if (IsPopupBoundary(element) || element is Page or TextBox or PasswordBox or RichEditBox
                or NumberBox or ComboBox or AutoSuggestBox or Slider or ScrollBar
                || (element is ScrollViewer and not DesktopWheelShelf))
                return false;
        }
        return false;
    }

    private static bool IsPopupBoundary(DependencyObject element)
        => element is Popup or ContentDialog or FlyoutPresenter or MenuFlyoutPresenter;

    private static PointerPoint? ReadPlainVerticalWheel(PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse || args.KeyModifiers != VirtualKeyModifiers.None)
            return null;
        var point = args.GetCurrentPoint(null);
        return point.Properties is
        {
            IsHorizontalMouseWheel: false, MouseWheelDelta: not 0,
            IsLeftButtonPressed: false, IsMiddleButtonPressed: false, IsRightButtonPressed: false,
        } ? point : null;
    }

    private sealed class WheelSequence
    {
        private const ulong IdleMicroseconds = 250_000;
        private ulong? _lastTick;
        private DesktopWheelShelf? _owner;
        private readonly PointerEventHandler _observe;

        internal WheelSequence(Page page)
        {
            _observe = Observe;
            // 纵向 ScrollViewer 通常已经处理了滚轮；页面仍需记住这个手势从纵向开始。
            page.AddHandler(PointerWheelChangedEvent, _observe, handledEventsToo: true);
            page.Unloaded += OnPageUnloaded;
        }

        internal int ShelfCount { get; set; }

        internal void Detach(Page page)
        {
            page.RemoveHandler(PointerWheelChangedEvent, _observe);
            page.Unloaded -= OnPageUnloaded;
        }

        internal void Forget(DesktopWheelShelf shelf)
        {
            if (ReferenceEquals(shelf, _owner)) _owner = null;
        }

        internal bool ContinuesOn(DesktopWheelShelf shelf, ulong timestamp)
        {
            Record(timestamp, shelf);
            return ReferenceEquals(_owner, shelf);
        }

        private void Record(ulong timestamp, DesktopWheelShelf? candidate)
        {
            if (_lastTick is not ulong previous || timestamp < previous || timestamp - previous >= IdleMicroseconds)
                _owner = candidate;
            _lastTick = timestamp;
        }

        private void Observe(object sender, PointerRoutedEventArgs args)
        {
            if (ReadPlainVerticalWheel(args) is not { } point) return;
            for (var element = args.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            {
                if (ReferenceEquals(element, sender))
                {
                    // 同一刻度冒泡到此时携带相同时间戳，不会重选轴或重复滚动。
                    Record(point.Timestamp, null);
                    return;
                }
                if (IsPopupBoundary(element)) return;
            }
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs args)
        {
            _lastTick = null;
            _owner = null;
        }
    }
}
