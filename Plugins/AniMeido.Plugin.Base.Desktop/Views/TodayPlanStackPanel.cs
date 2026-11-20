using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AniMeido.Plugin.Base.Views;

public sealed class TodayPlanStackPanel : Panel
{
    private bool _expanded;
    public bool Expanded
    {
        get => _expanded;
        set { if (_expanded == value) return; _expanded = value; InvalidateMeasure(); }
    }

    public int VisibleCardCount { get; set; } = 3;
    public double FanAngleStep { get; set; }
    public bool HideRearContent { get; set; }
    private readonly Dictionary<UIElement, (DependencyObject? Root, FrameworkElement? Face, List<(Control Control, bool TabStop)> Controls)> _cards = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 400;
        var height = 0d;
        for (var i = 0; i < Children.Count; ++i)
        {
            var child = Children[i];
            child.Measure(new Size(width, double.PositiveInfinity));
            if (Expanded || i < VisibleCardCount) height += child.DesiredSize.Height;
        }
        if (!Expanded && FanAngleStep != 0 && Children.Count > VisibleCardCount)
            height += Math.Min(3, Children.Count - VisibleCardCount) * 5;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var top = 0d;
        var anchor = Children.Take(Math.Max(0, VisibleCardCount - 1)).Sum(child => child.DesiredSize.Height);
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            var rear = !Expanded && index >= VisibleCardCount;
            var depth = index - VisibleCardCount + 1;
            child.Arrange(new Rect(0, rear ? anchor + depth * (FanAngleStep == 0 ? 0 : 5) : top, finalSize.Width, child.DesiredSize.Height));
            if (child.RenderTransform is not RotateTransform transform) child.RenderTransform = transform = new();
            transform.Angle = rear ? Math.Min(3, depth) * FanAngleStep : 0;
            child.Opacity = !rear || depth <= 3 ? 1 : 0;
            child.IsHitTestVisible = !rear;
            Canvas.SetZIndex(child, Children.Count - index);
            var state = CardState(child);
            if (state.Face is { } face) face.Opacity = rear && HideRearContent ? 0 : 1;
            foreach (var (control, tabStop) in state.Controls) control.IsTabStop = !rear && tabStop;
            top += child.DesiredSize.Height;
        }
        foreach (var stale in _cards.Keys.Where(item => !Children.Contains(item)).ToArray()) _cards.Remove(stale);
        return finalSize;
    }

    private (DependencyObject? Root, FrameworkElement? Face, List<(Control Control, bool TabStop)> Controls) CardState(UIElement child)
    {
        var root = VisualTreeHelper.GetChildrenCount(child) == 0 ? null : VisualTreeHelper.GetChild(child, 0);
        if (_cards.TryGetValue(child, out var state) && ReferenceEquals(root, state.Root)) return state;
        var controls = new List<(Control, bool)>();
        FrameworkElement? face = null;
        Collect(child);
        state = (root, face, controls);
        _cards[child] = state;
        return state;

        void Collect(DependencyObject node)
        {
            if (node is FrameworkElement { Name: "CardPackFace" } content) face = content;
            if (node is Control control) controls.Add((control, control.IsTabStop));
            // 框架控件负责自身的焦点；只收集我们的卡片和编辑器。
            if (node is Control and not UserControl) return;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Collect(VisualTreeHelper.GetChild(node, i));
        }
    }
}
