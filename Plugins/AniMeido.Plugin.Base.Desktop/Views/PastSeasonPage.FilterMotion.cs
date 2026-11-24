using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class PastSeasonPage
{
    private Dictionary<int, Point>? _filterOrigins;
    private Storyboard? _filterSlide;
    private readonly List<AnimeCard> _movingCards = [];

    private void CaptureFilterOrigins()
    {
        Dictionary<int, Point>? origins = null;
        // 从深处筛选会回到顶部，两屏之间不做跨屏飞行动画。
        if (_active && !_seasonPending && !ViewModel.IsLoading && CatalogScroller.VerticalOffset < 1
            && Application.Current.Resources.TryGetValue("DesktopMotionEnabled", out var motion) && motion is true)
        {
            origins = [];
            foreach (var card in _realizedCards)
                if (card.Anime is { } anime && ViewportPosition(card) is { } position)
                    origins[anime.ID] = position;
        }
        StopFilterMotion();
        _filterOrigins = origins is { Count: > 0 } ? origins : null;
    }

    private Point? ViewportPosition(AnimeCard card)
    {
        // 跨行复用时，新卡片已排好位置，但 Loaded 要到本轮布局结束后才触发。
        if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return null;
        var p = card.TransformToVisual(CatalogScroller).TransformPoint(default);
        return p.X < CatalogScroller.ViewportWidth && p.Y < CatalogScroller.ViewportHeight
            && p.X + card.ActualWidth > 0 && p.Y + card.ActualHeight > 0 ? p : null;
    }

    private void AnimateFilterResult()
    {
        var origins = _filterOrigins!;
        _filterOrigins = null;
        var slide = new Storyboard();
        foreach (var card in _realizedCards)
        {
            if (card.Anime is not { } anime || !origins.TryGetValue(anime.ID, out var old)
                || ViewportPosition(card) is not { } current || card.RenderTransform is not null) continue;
            // 两张卡往相反方向移动时也留出间隙，不让标题和邻卡的封面相互遮挡。
            var dx = Math.Clamp(old.X - current.X, -6, 6);
            var dy = Math.Clamp(old.Y - current.Y, -8, 8);
            if (Math.Abs(dx) < .5 && Math.Abs(dy) < .5) continue;
            var shift = new TranslateTransform { X = dx, Y = dy };
            card.RenderTransform = shift;
            _movingCards.Add(card);
            SlideAxis(nameof(TranslateTransform.X), dx);
            SlideAxis(nameof(TranslateTransform.Y), dy);

            void SlideAxis(string axis, double from)
            {
                if (Math.Abs(from) < .5) return;
                var move = new DoubleAnimation { From = from, To = 0, Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(move, shift);
                Storyboard.SetTargetProperty(move, axis);
                slide.Children.Add(move);
            }
        }
        if (_movingCards.Count == 0) return;
        _filterSlide = slide;
        slide.Completed += (_, _) => { if (ReferenceEquals(_filterSlide, slide)) StopFilterMotion(); };
        slide.Begin();
    }

    private void StopFilterMotion()
    {
        _filterOrigins = null;
        var slide = _filterSlide;
        _filterSlide = null;
        slide?.Stop();
        foreach (var card in _movingCards) card.ClearValue(RenderTransformProperty);
        _movingCards.Clear();
    }
}
