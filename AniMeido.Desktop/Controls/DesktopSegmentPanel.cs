using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AniMeido.App.Controls;

public sealed class DesktopSegmentPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return default;
        var naturalWidth = 0d;
        foreach (var item in Children)
        {
            item.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            naturalWidth = Math.Max(naturalWidth, item.DesiredSize.Width);
        }
        var width = Math.Min(naturalWidth * Children.Count, availableSize.Width);
        var height = 0d;
        foreach (var item in Children)
        {
            item.Measure(new Size(width / Children.Count, availableSize.Height));
            height = Math.Max(height, item.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        var width = finalSize.Width / Children.Count;
        for (var index = 0; index < Children.Count; index++)
            Children[index].Arrange(new Rect(index * width, 0, width, finalSize.Height));
        return finalSize;
    }
}
