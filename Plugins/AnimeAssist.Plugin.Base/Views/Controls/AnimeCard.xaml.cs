using AniMeido.Contracts.Models;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace AniMeido.Plugin.Base.Views.Controls;

public sealed partial class AnimeCard : UserControl
{
    public static readonly DependencyProperty ShowWeekdayBadgeProperty =
        DependencyProperty.Register(nameof(ShowWeekdayBadge), typeof(bool), typeof(AnimeCard),
            new PropertyMetadata(false, OnShowWeekdayBadgeChanged));

    public bool ShowWeekdayBadge
    {
        get => (bool)GetValue(ShowWeekdayBadgeProperty);
        set => SetValue(ShowWeekdayBadgeProperty, value);
    }

    private static readonly Uri PlaceholderUri = new("ms-appx:///Assets/Placeholder_cover.png");

    public AnimeCard()
    {
        InitializeComponent();

        DataContextChanged += (s, e) =>
        {
            UpdateWeekdayBadge();
            // 当 CoverURL 为空时使用占位图
            if (DataContext is Anime anime && string.IsNullOrEmpty(anime.CoverURL))
                CoverImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(PlaceholderUri);
        };
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;

        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        visual.CenterPoint = new System.Numerics.Vector3(
            (float)e.NewSize.Width / 2,
            (float)e.NewSize.Height / 2,
            0);
    }

    private void OnCoverImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        CoverImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(PlaceholderUri);
    }

    private static void OnShowWeekdayBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (AnimeCard)d;
        card.UpdateWeekdayBadge();
    }

    private void UpdateWeekdayBadge()
    {
        if (!ShowWeekdayBadge || DataContext is not Anime anime || !anime.AirDate.HasValue)
        {
            WeekdayBadge.Visibility = Visibility.Collapsed;
            return;
        }

        if (anime.AirDate.Value.DayOfWeek == DateTime.Now.DayOfWeek)
        {
            WeekdayBadgeText.Text = anime.AirDate.Value.DayOfWeek switch
            {
                DayOfWeek.Monday => "周一放送",
                DayOfWeek.Tuesday => "周二放送",
                DayOfWeek.Wednesday => "周三放送",
                DayOfWeek.Thursday => "周四放送",
                DayOfWeek.Friday => "周五放送",
                DayOfWeek.Saturday => "周六放送",
                DayOfWeek.Sunday => "周日放送",
                _ => ""
            };
            WeekdayBadge.Visibility = Visibility.Visible;
        }
        else
        {
            WeekdayBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = visual.Compositor;

        visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 16));

        var scaleX = compositor.CreateScalarKeyFrameAnimation();
        scaleX.InsertKeyFrame(1.0f, 1.05f);
        scaleX.Duration = TimeSpan.FromMilliseconds(200);

        var scaleY = compositor.CreateScalarKeyFrameAnimation();
        scaleY.InsertKeyFrame(1.0f, 1.05f);
        scaleY.Duration = TimeSpan.FromMilliseconds(200);

        visual.StartAnimation("Scale.X", scaleX);
        visual.StartAnimation("Scale.Y", scaleY);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = visual.Compositor;

        visual.CenterPoint = new System.Numerics.Vector3(
            (float)ActualWidth / 2,
            (float)ActualHeight / 2,
            0);

        visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 0));

        var scaleX = compositor.CreateScalarKeyFrameAnimation();
        scaleX.InsertKeyFrame(1.0f, 1.0f);
        scaleX.Duration = TimeSpan.FromMilliseconds(200);

        var scaleY = compositor.CreateScalarKeyFrameAnimation();
        scaleY.InsertKeyFrame(1.0f, 1.0f);
        scaleY.Duration = TimeSpan.FromMilliseconds(200);

        visual.StartAnimation("Scale.X", scaleX);
        visual.StartAnimation("Scale.Y", scaleY);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = visual.Compositor;

        var scaleX = compositor.CreateScalarKeyFrameAnimation();
        scaleX.InsertKeyFrame(1.0f, 0.95f);
        scaleX.Duration = TimeSpan.FromMilliseconds(100);

        var scaleY = compositor.CreateScalarKeyFrameAnimation();
        scaleY.InsertKeyFrame(1.0f, 0.95f);
        scaleY.Duration = TimeSpan.FromMilliseconds(100);

        visual.StartAnimation("Scale.X", scaleX);
        visual.StartAnimation("Scale.Y", scaleY);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = visual.Compositor;

        var scaleX = compositor.CreateScalarKeyFrameAnimation();
        scaleX.InsertKeyFrame(1.0f, 1.05f);
        scaleX.Duration = TimeSpan.FromMilliseconds(100);

        var scaleY = compositor.CreateScalarKeyFrameAnimation();
        scaleY.InsertKeyFrame(1.0f, 1.05f);
        scaleY.Duration = TimeSpan.FromMilliseconds(100);

        visual.StartAnimation("Scale.X", scaleX);
        visual.StartAnimation("Scale.Y", scaleY);
    }
}
