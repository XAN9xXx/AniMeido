using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AniMeido.Plugin.Base.Views;

// 一套真实卡片同时承担扇形与竖列布局；展开只改变位置，不重建控件。
public sealed class TodayPlanStackPanel : Panel
{
    internal bool IsAnimating { get; set; }
    public double FanAngleStep { get; set; } = 3;
    public bool HideRearContent { get; set; }
    private int _visibleCardCount = 3;
    public int VisibleCardCount
    {
        get => _visibleCardCount;
        set
        {
            var count = Math.Max(1, value);
            if (_visibleCardCount == count) return;
            _visibleCardCount = count;
            InvalidateMeasure();
        }
    }

    private bool _expanded;
    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            InvalidateMeasure();
        }
    }

    public double ExpandedHeight { get; private set; }
    public double CollapsedHeight { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 400;
        ExpandedHeight = 0;
        CollapsedHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            ExpandedHeight += child.DesiredSize.Height;
        }
        foreach (var child in Children.Take(VisibleCardCount))
            CollapsedHeight += child.DesiredSize.Height;
        if (Children.Count > VisibleCardCount)
        {
            var angle = Math.Min(3, Children.Count - VisibleCardCount) * FanAngleStep * Math.PI / 180;
            var cardHeight = Math.Max(0, Children[VisibleCardCount - 1].DesiredSize.Height - 8);
            CollapsedHeight += Math.Max(0, Math.Sin(angle) * width + (Math.Cos(angle) - 1) * cardHeight);
        }
        return new Size(width, Expanded ? ExpandedHeight : CollapsedHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        var fanOriginTop = Children.Take(VisibleCardCount - 1).Sum(child => child.DesiredSize.Height);
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            var height = child.DesiredSize.Height;
            var rear = index >= VisibleCardCount;
            var top = rear && !Expanded ? fanOriginTop : y;
            child.Arrange(new Rect(0, top, finalSize.Width, height));
            if (!IsAnimating)
            {
                if (child.RenderTransform is not CompositeTransform transform)
                    child.RenderTransform = transform = new CompositeTransform();
                child.RenderTransformOrigin = new Point(0, 0);
                transform.Rotation = rear ? Math.Min(index - VisibleCardCount + 1, 3) * FanAngleStep * (Expanded ? 0 : 1) : 0;
                child.Opacity = index >= VisibleCardCount + 3 && !Expanded ? 0 : 1;
                if (HideRearContent && GetCardFace(child) is { } face)
                    face.Opacity = rear && !Expanded ? 0 : 1;
            }
            Canvas.SetZIndex(child, Children.Count - index);
            // 收起的后排不可触发卡片操作；露出的区域由面板处理展开。
            child.IsHitTestVisible = !rear || Expanded;
            SetCardTabStops(child, !rear || Expanded);
            y += height;
        }
        return finalSize;
    }

    private static void SetCardTabStops(DependencyObject element, bool enabled)
    {
        // 当前计划模板的交互元素均为按钮；不禁用控件，避免改变后排卡片的颜色。
        if (element is ButtonBase button)
        {
            button.IsTabStop = enabled;
            return;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            SetCardTabStops(VisualTreeHelper.GetChild(element, index), enabled);
    }

    internal static UIElement? GetCardFace(DependencyObject element)
    {
        if (element is FrameworkElement { Name: "CardPackFace" } face) return face;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (GetCardFace(VisualTreeHelper.GetChild(element, index)) is { } childFace) return childFace;
        return null;
    }
}
