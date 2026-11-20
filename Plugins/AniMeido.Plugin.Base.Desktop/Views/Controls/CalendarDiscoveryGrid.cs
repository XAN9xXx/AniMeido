using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AniMeido.Plugin.Base.Views.Controls;

public sealed class CalendarDiscoveryGrid : Grid
{
    internal Action<double>? PreparingLayout { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 布局宽度在这里已知，还没有摆放子控件，可以在第一轮就确定行列。
        if (availableSize.Width > 0 && double.IsFinite(availableSize.Width))
            PreparingLayout?.Invoke(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }
}
