using AniMeido.Contracts.DragDrop;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace AniMeido.Plugin.Base.Views.Controls
{
    /// <summary>
    /// [旧内部拖拽路径事件参数] 包含拖拽的 Anime 对象、指针位置和统一载荷。
    /// 此事件类仅由旧 DragTriggered 事件使用，标准拖拽路径不经过此类型。
    /// Payload 构造逻辑与标准拖拽一致，确保数据格式统一。
    /// </summary>
    public class AnimeDragEventArgs : EventArgs
    {
        public Anime Anime { get; }
        public Point PointerPosition { get; }
        public UIElement Source { get; }
        /// <summary>统一拖拽载荷（构造时从 Anime 构建）。</summary>
        public AnimeCardDragPayload? Payload { get; }

        public AnimeDragEventArgs(Anime anime, Point pointerPosition, UIElement source)
        {
            Anime = anime;
            PointerPosition = pointerPosition;
            Source = source;
            Payload = BuildPayload(anime);
        }

        private static AnimeCardDragPayload BuildPayload(Anime anime)
        {
            var payload = new AnimeCardDragPayload
            {
                AnimeId = anime.ID,
                Title = anime.Title,
                CoverImageUrl = anime.CoverURL,
                Summary = anime.Description,
                SeasonYear = anime.SeasonYear,
                SeasonMonth = anime.SeasonMonth,
                Source = "AnimeCard",
            };
            return payload;
        }
    }

    public sealed partial class AnimeCard : UserControl
    {
        /// <summary>
        /// [旧内部拖拽路径] 当检测到拖动手势（长按+移动阈值）时触发。
        /// 当前标准拖拽已启用（CanDrag=True），此事件在标准拖拽路径中不触发。
        /// 保留供旧页面兼容，不作为 AnimeCard 主拖拽入口。
        /// </summary>
        public event EventHandler<AnimeDragEventArgs>? DragTriggered;

        private bool _dragPointerDown;
        private Point _dragStartPoint;
        private int _coverLoadVersion;      // 封面加载版本号，防止旧异步任务更新新卡片
        private int _currentAnimeId;        // 当前显示番剧 ID，用于 DataContextChanged 快速判断
        private bool _snapshotCaptured;     // 防止重复触发 RenderTargetBitmap 截图
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

            // 预捕获：鼠标按下时即启动 RenderTargetBitmap 截图，为 GhostCard 准备视觉快照
            // 这样在拖拽真正开始时，snapshot 可能已经就绪
            if (!_snapshotCaptured)
            {
                _snapshotCaptured = true;
                _ = CaptureGhostSnapshotAsync();
            }
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _dragPointerDown = false;
            _snapshotCaptured = false;

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
            _snapshotCaptured = false;
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _dragPointerDown = false;
            _snapshotCaptured = false;
        }

        private void OnDragPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragPointerDown || DataContext is not Anime anime)
                return;

            // 标准拖拽启用后，旧指针拖拽不应再触发
            if (CanDrag)
            {
                _dragPointerDown = false;
                return;
            }

            var pt = e.GetCurrentPoint(this).Position;
            var dx = pt.X - _dragStartPoint.X;
            var dy = pt.Y - _dragStartPoint.Y;
            if (Math.Abs(dx) < 8 && Math.Abs(dy) < 8)
                return;

            // 达到阈值，触发拖动手势
            _dragPointerDown = false;
            DragTriggered?.Invoke(this, new AnimeDragEventArgs(anime, e.GetCurrentPoint(this).Position, this));
        }

        /// <summary>
        /// AnimeCard 本体标准拖拽源 — 当前拖拽系统主路径。
        /// 使用 AnimeCardDragPayload 作为跨窗口/跨区域的统一拖拽数据事实。
        /// payload 序列化为 JSON 后通过 StandardDataFormats.Text 传递。
        /// 同时设置 DragGhostCard 视觉定位上下文，使 GhostCard 跟随鼠标时
        /// 保持与原始按下位置一致的偏移。
        /// </summary>
        private void OnBodyDragStarting(UIElement sender, DragStartingEventArgs args)
        {
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

            // 记录 GhostCard 视觉定位上下文（不阻塞拖拽）
            var pointerPos = args.GetPosition(this);
            var context = new Services.AnimeCardDragVisualContext
            {
                PointerOffsetX = pointerPos.X,
                PointerOffsetY = pointerPos.Y,
                SourceCardWidth = ActualWidth,
                SourceCardHeight = ActualHeight,
                CoverImageSource = CoverImage.Source,
            };
            Services.AnimeCardDragVisualContext.Current = context;

            System.Diagnostics.Debug.WriteLine($"[AnimeCard] DragStarting: AllowedOperations=Copy, payload animeId={payload.AnimeId}, title={payload.Title}");
        }

        /// <summary>
        /// 使用 RenderTargetBitmap 截取当前 AnimeCard 控件的完整视觉快照。
        /// 快照存入 AnimeCardDragVisualContext.GhostSnapshotSource，
        /// 完成后触发 OnSnapshotReady 回调供 DragDropService 热更新 GhostCard。
        ///
        /// 在 PointerPressed 中预执行，不阻塞拖拽。
        /// 捕获前检查 ActualWidth/Height > 0，避免捕获未 Layout 的元素。
        /// </summary>
        private async Task CaptureGhostSnapshotAsync()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
            {
                System.Diagnostics.Debug.WriteLine("[AnimeCard] GhostSnapshot skipped: card not laid out");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[AnimeCard] GhostSnapshot capture started");
            try
            {
                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(this, (int)ActualWidth, (int)ActualHeight);

                var context = Services.AnimeCardDragVisualContext.Current;
                if (context != null)
                {
                    context.GhostSnapshotSource = rtb;
                    System.Diagnostics.Debug.WriteLine($"[AnimeCard] GhostSnapshot captured: {ActualWidth}x{ActualHeight}");
                }

                // 通知 DragDropService snapshot 已就绪
                Services.AnimeCardDragVisualContext.OnSnapshotReady?.Invoke();
            }
#pragma warning disable CA1031 // 截图失败不影响拖拽
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AnimeCard] GhostSnapshot capture failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        /// <summary>
        /// 拖拽启动阶段自兜底：鼠标仍在 AnimeCard 上方时，
        /// Page/Shell DropHost 可能尚未接管第一帧 DragOver。
        /// 设置 AcceptedOperation = Copy，并尽早触发 GhostCard 显示。
        /// </summary>
        private void OnSelfDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.Handled = true;
                e.DragUIOverride.IsCaptionVisible = false;
                e.DragUIOverride.IsGlyphVisible = false;
                e.DragUIOverride.IsContentVisible = false;

                // 尽早显示 GhostCard：通知 DragDropService 在拖拽源上触发视觉更新
                Services.AnimeCardDragVisualContext.OnSourceDragOver?.Invoke(e, this);
            }
        }

        /// <summary>
        /// 分享拖拽手柄的标准 DataPackage 拖放 — 跨窗口拖拽的辅助入口 / fallback。
        /// 使用 JSON 序列化 AnimeCardDragPayload，payload 格式与 AnimeCard 本体拖拽（OnBodyDragStarting）完全一致。
        /// 与本体拖拽并行存在，不冲突，不合并。
        /// 保留作为 ChatWindow 等跨窗口场景的备用拖拽入口。
        /// </summary>
        private void OnShareDragStarting(UIElement sender, DragStartingEventArgs args)
        {
            if (DataContext is not Anime anime)
            {
                args.Cancel = true;
                return;
            }

            System.Diagnostics.Debug.WriteLine("[ShareDrag] Share drag handle DragStarting triggered");

            var payload = new AnimeCardDragPayload
            {
                AnimeId = anime.ID,
                Title = anime.Title,
                CoverImageUrl = anime.CoverURL,
                Summary = anime.Description,
                SeasonYear = anime.SeasonYear,
                SeasonMonth = anime.SeasonMonth,
                Source = "ShareHandle",
            };

            var json = AnimeCardDragPayloadSerializer.Serialize(payload);
            args.Data.SetText(json);
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

            System.Diagnostics.Debug.WriteLine($"[ShareDrag] Share drag handle SetText payload success: {payload.AnimeId} - {payload.Title}");
        }
    }
}