using AniMeido.Contracts.DragDrop;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace AniMeido.Plugin.Base.Views.Controls
{
    /// <summary>单击 AnimeCard 时的事件参数。</summary>
    public sealed class AnimeCardClickedEventArgs : EventArgs
    {
        public Anime Anime { get; }
        public AnimeCardClickedEventArgs(Anime anime) => Anime = anime;
    }

    public sealed partial class AnimeCard : UserControl
    {
        /// <summary>单击 AnimeCard 时触发（非拖拽）。由页面订阅。</summary>
        public event EventHandler<AnimeCardClickedEventArgs>? CardClicked;

        // click-vs-drag 输入状态
        private bool _pointerDown;
        private bool _clickCandidate;
        private bool _standardDragStarted;
        private Point _pointerDownPoint;
        private const double ClickMoveThreshold = 8.0;

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
            PointerCaptureLost += OnPointerCaptureLost;
            PointerMoved += OnDragPointerMoved;

            // 拖拽启动阶段自兜底：鼠标仍在卡片上方时防止禁止图标
            AllowDrop = true;
            AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnSelfDragOver), true);

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
            _pointerDown = true;
            _clickCandidate = true;
            _standardDragStarted = false;
            _pointerDownPoint = e.GetCurrentPoint(this).Position;

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
            // 检测本次是否为单击——先捕获 DataContext，再判断状态
            Anime? clickAnime = DataContext as Anime;
            bool shouldClick = _clickCandidate && !_standardDragStarted && clickAnime != null;

            // 重置状态
            _pointerDown = false;
            _clickCandidate = false;
            _standardDragStarted = false;

            // 触发单击事件
            if (shouldClick)
                CardClicked?.Invoke(this, new AnimeCardClickedEventArgs(clickAnime!));

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
            _pointerDown = false;
            _clickCandidate = false;
            _standardDragStarted = false;
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _pointerDown = false;
            _clickCandidate = false;
            _standardDragStarted = false;
        }

        private void OnDragPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerDown)
                return;

            var pt = e.GetCurrentPoint(this).Position;
            var moved = Math.Abs(pt.X - _pointerDownPoint.X) >= ClickMoveThreshold
                     || Math.Abs(pt.Y - _pointerDownPoint.Y) >= ClickMoveThreshold;

            if (moved)
                _clickCandidate = false;

            // CanDrag=True：标准拖拽由 WinUI 管理，不干预，不设置 _pointerDown = false
            // CanDrag=False 路径已删除（所有 AnimeCard 已启用标准拖拽）
            if (CanDrag)
                return;

            // 不再支持 legacy pointer drag，但保留 _clickCandidate 已由 moved 更新
        }

        /// <summary>
        /// AnimeCard 本体标准拖拽源 — 当前拖拽系统主路径。
        /// 使用 AnimeCardDragPayload 作为跨窗口/跨区域的统一拖拽数据事实。
        /// payload 序列化为 JSON 后通过 StandardDataFormats.Text 传递。
        /// </summary>
        private void OnBodyDragStarting(UIElement sender, DragStartingEventArgs args)
        {
            // 一进入 DragStarting 就标记，确保 PointerReleased 不会误判
            _standardDragStarted = true;
            _clickCandidate = false;

            if (DataContext is not Anime anime)
            {
                args.Cancel = true;
                return;
            }

            System.Diagnostics.Debug.WriteLine("[AnimeCard] standard DragStarting triggered");

            var payload = new AnimeCardDragPayload
            {
                AnimeId = anime.ID,
                Title = anime.Title,
                CoverImageUrl = anime.CoverURL,
                Summary = anime.Description,
                SeasonYear = anime.SeasonYear,
                SeasonMonth = anime.SeasonMonth,
                Source = "AnimeCardBody",
            };

            var json = AnimeCardDragPayloadSerializer.Serialize(payload);
            args.Data.SetText(json);
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            args.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

            System.Diagnostics.Debug.WriteLine($"[AnimeCard] DragStarting: AllowedOperations=Copy, payload animeId={payload.AnimeId}, title={payload.Title}");

            // 尝试设置圆形封面 DragToken 视觉，失败时静默 fallback 到系统默认视觉
            AnimeCardDragTokenVisualFactory.TryApplyDragToken(args, anime);
        }

        /// <summary>
        /// 拖拽启动阶段自兜底：鼠标仍在 AnimeCard 上方时，
        /// Page/Shell DropHost 可能尚未接管第一帧 DragOver。
        /// 仅设置 AcceptedOperation = Copy，不执行业务。
        /// </summary>
        private void OnSelfDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.Handled = true;
                e.DragUIOverride.IsCaptionVisible = false;
                e.DragUIOverride.IsGlyphVisible = false;
                e.DragUIOverride.IsContentVisible = true;
            }
        }


    }
}