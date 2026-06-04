using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace AniMeido.Plugin.Base.Views.Controls
{
    public class AnimeDragEventArgs : EventArgs
    {
        public Anime Anime { get; }
        public Point PointerPosition { get; }
        public UIElement Source { get; }

        public AnimeDragEventArgs(Anime anime, Point pointerPosition, UIElement source)
        {
            Anime = anime;
            PointerPosition = pointerPosition;
            Source = source;
        }
    }

    public sealed partial class AnimeCard : UserControl
    {
        /// <summary>当检测到拖动手势（长按+移动阈值）时触发。</summary>
        public event EventHandler<AnimeDragEventArgs>? DragTriggered;

        private bool _dragPointerDown;
        private Point _dragStartPoint;
        private int _coverLoadVersion;      // 封面加载版本号，防止旧异步任务更新新卡片
        private int _currentAnimeId;        // 当前显示番剧 ID，用于 DataContextChanged 快速判断
        public static readonly DependencyProperty ShowWeekdayBadgeProperty =
            DependencyProperty.Register(nameof(ShowWeekdayBadge), typeof(bool), typeof(AnimeCard),
                new PropertyMetadata(false, OnShowWeekdayBadgeChanged));

        public bool ShowWeekdayBadge
        {
            get => (bool)GetValue(ShowWeekdayBadgeProperty);
            set => SetValue(ShowWeekdayBadgeProperty, value);
        }

        private static readonly Uri PlaceholderUri = ImageCacheHelper.PlaceholderUri;

        public AnimeCard()
        {
            InitializeComponent();

            DataContextChanged += (s, e) =>
            {
                UpdateWeekdayBadge();
                UpdateScoreBadge();
                if (DataContext is Anime anime)
                {
                    if (string.IsNullOrEmpty(anime.CoverURL))
                    {
                        CoverImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(PlaceholderUri);
                        HideRetryOverlay();
                    }
                    else
                    {
                        // 递增版本号并记录当前 Anime ID，用于防止旧异步任务污染新卡片
                        _coverLoadVersion++;
                        _currentAnimeId = anime.ID;
                        HideRetryOverlay();

                        // 指定解码宽度 300（应对 2x 缩放），避免全分辨率解码
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        bmp.DecodePixelWidth = 300;
                        bmp.UriSource = ImageCacheHelper.GetImageUri(anime.ID, anime.CoverURL);
                        CoverImage.Source = bmp;

                        // 后台下载缓存，成功后热更新当前卡片封面
                        if (!ImageCacheHelper.HasLocalCache(anime.ID))
                            _ = CacheAndUpdateAsync(anime);
                    }
                }
            };
            PointerEntered += OnPointerEntered;
            PointerExited += OnPointerExited;
            PointerPressed += OnPointerPressed;
            PointerReleased += OnPointerReleased;
            PointerCanceled += OnPointerCanceled;
            PointerMoved += OnDragPointerMoved;

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

        /// <summary>
        /// 后台下载缓存，成功后热更新当前卡片封面（不需要等 GridView 回收）。
        /// </summary>
        private async Task CacheAndUpdateAsync(Anime anime)
        {
            var version = _coverLoadVersion;

            if (!await ImageCacheHelper.CacheImageAsync(anime.ID, anime.CoverURL!))
                return;

            // 缓存下载完成，检查版本号防止更新到已复用的卡片
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_coverLoadVersion != version) return;
                if (!ReferenceEquals(DataContext, anime)) return;
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bmp.DecodePixelWidth = 300;
                bmp.UriSource = ImageCacheHelper.GetImageUri(anime.ID, anime.CoverURL);
                CoverImage.Source = bmp;
            });
        }

        private async void OnCoverImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            var dispatcher = DispatcherQueue;

            CoverImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(PlaceholderUri);
            ShowRetryOverlay();

            if (DataContext is Anime anime && !string.IsNullOrEmpty(anime.CoverURL))
            {
                var version = _coverLoadVersion;

                bool success = false;
                for (int retry = 0; retry < 3; retry++)
                {
                    // 检查卡片是否已被回收复用
                    if (_coverLoadVersion != version) return;

                    if (await ImageCacheHelper.CacheImageAsync(anime.ID, anime.CoverURL))
                    {
                        success = true;
                        break;
                    }
                    await Task.Delay(3000);
                }

                await Task.Delay(500);

                dispatcher.TryEnqueue(() =>
                {
                    if (_coverLoadVersion != version) return;
                    if (ReferenceEquals(DataContext, anime) && success && ImageCacheHelper.HasLocalCache(anime.ID))
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        bmp.DecodePixelWidth = 300;
                        bmp.UriSource = ImageCacheHelper.GetImageUri(anime.ID, anime.CoverURL);
                        CoverImage.Source = bmp;
                        HideRetryOverlay();
                    }
                    else
                    {
                        ShowFailedOverlay();
                    }
                });
            }
            else
            {
                HideRetryOverlay();
            }
        }

        private void ShowRetryOverlay()
        {
            if (RetryOverlay == null) return;
            RetryRing.Visibility = Visibility.Visible;
            RetryOverlay.Visibility = Visibility.Visible;
            RetryRing.IsActive = true;
        }

        private void ShowFailedOverlay()
        {
            if (RetryOverlay == null) return;
            RetryRing.Visibility = Visibility.Collapsed;
            RetryRing.IsActive = false;
            // 保持覆盖层可见，作为失败视觉提示
        }

        private void HideRetryOverlay()
        {
            if (RetryOverlay == null) return;
            RetryOverlay.Visibility = Visibility.Collapsed;
            RetryRing.IsActive = false;
            RetryRing.Visibility = Visibility.Visible;
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

        private void UpdateScoreBadge()
        {
            if (DataContext is Anime anime && anime.Score.HasValue && anime.Score.Value > 0)
            {
                ScoreText.Text = anime.Score.Value.ToString("F1");
                ScoreBadge.Visibility = Visibility.Visible;
            }
            else
            {
                ScoreBadge.Visibility = Visibility.Collapsed;
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
            _dragPointerDown = true;
            _dragStartPoint = e.GetCurrentPoint(this).Position;

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
            _dragPointerDown = false;

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

        private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _dragPointerDown = false;
        }

        private void OnDragPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragPointerDown || DataContext is not Anime anime)
                return;

            var pt = e.GetCurrentPoint(this).Position;
            var dx = pt.X - _dragStartPoint.X;
            var dy = pt.Y - _dragStartPoint.Y;
            if (Math.Abs(dx) < 8 && Math.Abs(dy) < 8)
                return;

            // 达到阈值，触发拖动手势
            _dragPointerDown = false;
            DragTriggered?.Invoke(this, new AnimeDragEventArgs(anime, e.GetCurrentPoint(this).Position, this));
        }
    }
}