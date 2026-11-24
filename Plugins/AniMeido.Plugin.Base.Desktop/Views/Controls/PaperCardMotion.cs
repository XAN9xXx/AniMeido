using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using System.Diagnostics;

namespace AniMeido.Plugin.Base.Views.Controls;

// 两端都在页面坐标系中测量。行被回收后，只保留作品标识、矩形和一张已显示的封面。
internal sealed record PaperCardPose(Rect Sheet, Rect Cover, Rect Title, double FontSize, bool CoverVisible = true, bool TitleVisible = true)
{
    internal static Rect Bounds(FrameworkElement element, UIElement relativeTo)
        => element.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
}

internal sealed class PaperCardTrip(int id, string title, ImageSource? image, PaperCardPose origin)
{
    internal int Id { get; } = id;
    internal string Title { get; } = title;
    internal ImageSource? Image { get; } = image;
    internal PaperCardPose Origin { get; } = origin;
    internal PaperCardPose? Detail { get; set; }
    internal static bool Enabled => Application.Current.Resources.TryGetValue("DesktopPaperCardMotionEnabled", out var enabled) && enabled is true;
}

internal sealed class PaperCardMotion(Canvas layer, FrameworkElement body)
{
    private Storyboard? _animation;
    private Storyboard? _reveal;
    private PaperCardPose? _destination;
    private long _started;
    private bool _bodyAcceptedInput;
    internal bool Active => layer.Children.Count != 0;
    internal bool Waiting => Active && _destination is null;

    internal void Hold(PaperCardTrip trip, PaperCardPose from, bool suspendInput = false)
    {
        Stop();
        var resources = Application.Current.Resources;
        var sheet = new Border { Background = (Brush)resources["DesktopPageBrush"], BorderBrush = (Brush)resources["DesktopLineBrush"],
            BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)resources["DesktopControlRadius"] };
        var cover = new Border { Opacity = from.CoverVisible ? 1 : 0, Background = (Brush)resources["DesktopPlaceholderBrush"], CornerRadius = (CornerRadius)resources["DesktopCoverRadius"],
            Child = new Image { Source = trip.Image, Stretch = Stretch.UniformToFill } };
        var title = new TextBlock { Text = trip.Title, Opacity = from.TitleVisible ? 1 : 0, FontFamily = (FontFamily)resources["DesktopUiFontFamily"],
            FontSize = from.FontSize, Foreground = (Brush)resources["DesktopTextBrush"], TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
        layer.Children.Add(sheet);
        layer.Children.Add(cover);
        layer.Children.Add(title);
        Place(sheet, from.Sheet);
        Place(cover, from.Cover);
        Place(title, from.Title);
        _bodyAcceptedInput = body.IsHitTestVisible;
        if (suspendInput) body.IsHitTestVisible = false;
        // 展开时正文待命；收回时列表在纸面后排好，不先让整片列表变透明。
        body.Opacity = suspendInput ? 0 : 1;
    }

    internal void Play(PaperCardPose from, PaperCardPose to, Action? expanded = null)
    {
        if (!Waiting) return;
        _started = Stopwatch.GetTimestamp();
        Animate(from, to, 410, expanded);
    }

    internal void FollowDetail(PaperCardPose to, Action expanded)
    {
        if (!Active || _destination is null || _destination == to) return;
        if (_animation is null)
        {
            SettleAt(to);
            _destination = to;
            return;
        }
        // 重新排版时从正在显示的位置续走。沿用原来的结束时刻，不重新播放或延长开场。
        var title = (TextBlock)layer.Children[2];
        Rect Current(int i) => PaperCardPose.Bounds((FrameworkElement)layer.Children[i], layer);
        var from = new PaperCardPose(Current(0), Current(1), Current(2),
            title.FontSize * ((CompositeTransform)title.RenderTransform).ScaleX);
        var old = _animation;
        _animation = null;
        old.Stop();
        Animate(from, to, Math.Max(1, 410 - Stopwatch.GetElapsedTime(_started).TotalMilliseconds), expanded);
    }

    private void Animate(PaperCardPose from, PaperCardPose to, double milliseconds, Action? expanded)
    {
        _destination = to;
        var slide = new Storyboard();
        _animation = slide;
        Move((FrameworkElement)layer.Children[0], from.Sheet, to.Sheet);
        // 详情向下滚动后，已离屏的内容由列表淡入，只有仍可见的元素参与位移。
        if (from.CoverVisible) Move((FrameworkElement)layer.Children[1], from.Cover, to.Cover);
        ((TextBlock)layer.Children[2]).FontSize = to.FontSize;
        if (from.TitleVisible) Move((FrameworkElement)layer.Children[2], from.Title, to.Title, from.FontSize / to.FontSize);
        slide.Duration = TimeSpan.FromMilliseconds(milliseconds);
        slide.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_animation, slide)) return;
            if (expanded is null) { Stop(); return; }
            _animation = null;
            SettleAt(to);
            slide.Stop();
            expanded();
        };
        slide.Begin();

        void Move(FrameworkElement element, Rect start, Rect end, double? uniform = null)
        {
            Place(element, end);
            var t = new CompositeTransform { TranslateX = start.X - end.X, TranslateY = start.Y - end.Y,
                ScaleX = uniform ?? start.Width / end.Width, ScaleY = uniform ?? start.Height / end.Height };
            element.RenderTransform = t;
            Track(t, nameof(t.TranslateX), t.TranslateX, 0);
            Track(t, nameof(t.TranslateY), t.TranslateY, 0);
            Track(t, nameof(t.ScaleX), t.ScaleX, 1);
            Track(t, nameof(t.ScaleY), t.ScaleY, 1);
        }

        void Track(DependencyObject target, string property, double start, double end)
        {
            if (start == end) return;
            var track = new DoubleAnimationUsingKeyFrames();
            track.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = start });
            track.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(milliseconds), Value = end,
                KeySpline = new KeySpline { ControlPoint1 = new Point(.25, .06), ControlPoint2 = new Point(.15, 1) } });
            Storyboard.SetTarget(track, target);
            Storyboard.SetTargetProperty(track, property);
            slide.Children.Add(track);
        }
    }

    private void SettleAt(PaperCardPose to)
    {
        var sheet = (FrameworkElement)layer.Children[0];
        var cover = (FrameworkElement)layer.Children[1];
        var title = (TextBlock)layer.Children[2];
        sheet.RenderTransform = cover.RenderTransform = title.RenderTransform = null;
        Place(sheet, to.Sheet);
        Place(cover, to.Cover);
        Place(title, to.Title);
        title.FontSize = to.FontSize;
    }

    internal void RevealBody(IEnumerable<UIElement>? newContent = null)
    {
        if (!Active || _reveal is not null) return;
        var fade = new Storyboard();
        _reveal = fade;
        // 底色已由详情页接手。半透明的纸面或占位底叠在两张相同封面之间，会让封面先变灰再恢复。
        ((Border)layer.Children[0]).Background = null;
        var cover = (Border)layer.Children[1];
        if (cover.Child is Image { Source: not null }) cover.Background = null;
        body.Opacity = 1;
        Fade(layer, 1, 0);
        if (newContent is not null)
            foreach (var element in newContent) Fade(element, 0, element.Opacity);
        fade.Completed += (_, _) => { if (ReferenceEquals(_reveal, fade)) Stop(); };
        fade.Begin();

        void Fade(UIElement element, double from, double to)
        {
            var track = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(190),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            Storyboard.SetTarget(track, element);
            Storyboard.SetTargetProperty(track, nameof(UIElement.Opacity));
            fade.Children.Add(track);
        }
    }

    private static void Place(FrameworkElement element, Rect bounds)
    {
        Canvas.SetLeft(element, bounds.X);
        Canvas.SetTop(element, bounds.Y);
        element.Width = bounds.Width;
        element.Height = bounds.Height;
    }

    internal void Stop()
    {
        if (layer.Children.Count == 0) return;
        var animation = _animation;
        var reveal = _reveal;
        _animation = null;
        _reveal = null;
        _destination = null;
        body.Opacity = 1;
        layer.Children.Clear();
        reveal?.Stop();
        animation?.Stop();
        body.IsHitTestVisible = _bodyAcceptedInput;
        layer.Opacity = 1;
    }
}
