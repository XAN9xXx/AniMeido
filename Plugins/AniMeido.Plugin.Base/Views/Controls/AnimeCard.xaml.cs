using AniMeido.Contracts.DragDrop;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace AniMeido.Plugin.Base.Views.Controls
{
    /// <summary>单击 AnimeCard 时的事件参数。</summary>
    public sealed class AnimeCardClickedEventArgs : EventArgs
    {
        public Anime Anime { get; }
        public AnimeCardClickedEventArgs(Anime anime) => Anime = anime;
    }

    /// <summary>点击卡片上的悬停标记按钮时的事件参数。</summary>
    public sealed class AnimeCardTrackingActionEventArgs : EventArgs
    {
        public Anime Anime { get; }
        public AnimeTrackingStatus Status { get; }
        public AnimeCardTrackingActionEventArgs(Anime anime, AnimeTrackingStatus status)
        {
            Anime = anime;
            Status = status;
        }
    }

    public sealed partial class AnimeCard : UserControl
    {
        /// <summary>单击 AnimeCard 时触发（非拖拽）。由页面订阅。</summary>
        public event EventHandler<AnimeCardClickedEventArgs>? CardClicked;

        /// <summary>点击悬停标记按钮时触发；是否写入或取消由页面决定。</summary>
        public event EventHandler<AnimeCardTrackingActionEventArgs>? TrackingActionRequested;

        private static readonly IReadOnlyDictionary<AnimeTrackingStatus, string> StatusLabels =
            TrackingActionDescriptor.CreateDefaults()
                .ToDictionary(action => action.Status, action => action.ActiveLabel);

        private bool _isPointerOver;
        private bool _hasKeyboardFocus;
        private object? _hoverStateOwner;
        private readonly ScaleTransform _cardScale = new();

        // click-vs-drag 输入状态
        private bool _pointerDown;
        private bool _clickCandidate;
        private bool _standardDragStarted;
        private Point _pointerDownPoint;
        private const double ClickMoveThreshold = 8.0;
        // 标记写入中快捷按钮的不透明度。
        private const double PendingActionOpacity = 0.55;

        private readonly Brush _highScoreBrush;

        /// <summary>卡片宽度（不含外边距）。番剧库按窗口宽度设置，其他页面保持默认。</summary>
        public static readonly DependencyProperty CardWidthProperty =
            DependencyProperty.Register(
                nameof(CardWidth),
                typeof(double),
                typeof(AnimeCard),
                new PropertyMetadata(AnimeCardPresentation.DefaultCardWidth, OnCardWidthChanged));

        public double CardWidth
        {
            get => (double)GetValue(CardWidthProperty);
            set => SetValue(CardWidthProperty, value);
        }

        /// <summary>日期显示为“4月8日 · 周三”（番剧库使用，年份已由季度确定）。</summary>
        public static readonly DependencyProperty ShowSeasonDateProperty =
            DependencyProperty.Register(
                nameof(ShowSeasonDate),
                typeof(bool),
                typeof(AnimeCard),
                new PropertyMetadata(false, OnShowSeasonDateChanged));

        public bool ShowSeasonDate
        {
            get => (bool)GetValue(ShowSeasonDateProperty);
            set => SetValue(ShowSeasonDateProperty, value);
        }

        public static readonly DependencyProperty ShowWeekdayBadgeProperty =
            DependencyProperty.Register(nameof(ShowWeekdayBadge), typeof(bool), typeof(AnimeCard),
                new PropertyMetadata(false, OnShowWeekdayBadgeChanged));

        public bool ShowWeekdayBadge
        {
            get => (bool)GetValue(ShowWeekdayBadgeProperty);
            set => SetValue(ShowWeekdayBadgeProperty, value);
        }

        public static readonly DependencyProperty ShowMediaFormatBadgeProperty =
            DependencyProperty.Register(
                nameof(ShowMediaFormatBadge),
                typeof(bool),
                typeof(AnimeCard),
                new PropertyMetadata(false, OnShowMediaFormatBadgeChanged));

        public bool ShowMediaFormatBadge
        {
            get => (bool)GetValue(ShowMediaFormatBadgeProperty);
            set => SetValue(ShowMediaFormatBadgeProperty, value);
        }

        /// <summary>在封面左上角显示的追番状态（放送日历使用）。</summary>
        public static readonly DependencyProperty TrackingStatusProperty =
            DependencyProperty.Register(
                nameof(TrackingStatus),
                typeof(AnimeTrackingStatus),
                typeof(AnimeCard),
                new PropertyMetadata(
                    AnimeTrackingStatus.None,
                    OnTrackingPresentationChanged));

        public AnimeTrackingStatus TrackingStatus
        {
            get => (AnimeTrackingStatus)GetValue(TrackingStatusProperty);
            set => SetValue(TrackingStatusProperty, value);
        }

        /// <summary>悬停时显示“追番 / 关注”标记按钮（放送日历使用）。</summary>
        public static readonly DependencyProperty ShowQuickActionsProperty =
            DependencyProperty.Register(
                nameof(ShowQuickActions),
                typeof(bool),
                typeof(AnimeCard),
                new PropertyMetadata(false, OnTrackingPresentationChanged));

        public bool ShowQuickActions
        {
            get => (bool)GetValue(ShowQuickActionsProperty);
            set => SetValue(ShowQuickActionsProperty, value);
        }

        /// <summary>为 false 时隐藏“追番”，只保留“关注”并占满整行（未上映的剧场版、OVA）。</summary>
        public static readonly DependencyProperty ShowWatchActionProperty =
            DependencyProperty.Register(
                nameof(ShowWatchAction),
                typeof(bool),
                typeof(AnimeCard),
                new PropertyMetadata(true, OnTrackingPresentationChanged));

        public bool ShowWatchAction
        {
            get => (bool)GetValue(ShowWatchActionProperty);
            set => SetValue(ShowWatchActionProperty, value);
        }

        /// <summary>
        /// 快捷标记正在写入：两个按钮变淡，点击被按钮接住但不再发出请求。
        /// 不禁用按钮，禁用会把按钮上的焦点挤走；也不关闭命中测试，否则点击会落到卡片上打开详情。
        /// </summary>
        public static readonly DependencyProperty IsTrackingActionPendingProperty =
            DependencyProperty.Register(
                nameof(IsTrackingActionPending),
                typeof(bool),
                typeof(AnimeCard),
                new PropertyMetadata(false, OnTrackingActionPendingChanged));

        public bool IsTrackingActionPending
        {
            get => (bool)GetValue(IsTrackingActionPendingProperty);
            set => SetValue(IsTrackingActionPendingProperty, value);
        }

        /// <summary>在封面右上角显示的放送星期（放送日历搜索结果使用）。</summary>
        public static readonly DependencyProperty WeekdayTextProperty =
            DependencyProperty.Register(
                nameof(WeekdayText),
                typeof(string),
                typeof(AnimeCard),
                new PropertyMetadata(null, OnWeekdayTextChanged));

        public string? WeekdayText
        {
            get => (string?)GetValue(WeekdayTextProperty);
            set => SetValue(WeekdayTextProperty, value);
        }

        public AnimeCard()
        {
            InitializeComponent();
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = _cardScale;
            _highScoreBrush = ScoreBadge.Background;

            DataContextChanged += (s, e) =>
            {
                UpdateWeekdayBadge();
                UpdateMediaFormatBadge();
                UpdateAirDateText();
                // 容器复用时换了作品，悬停状态不沿用。
                // 焦点首次进入列表时，所有卡片会以同一作品再触发一次本事件，
                // 此时清掉悬停会让按钮在按下途中隐藏、丢失指针捕获，点击随之失效。
                if (!ReferenceEquals(e.NewValue, _hoverStateOwner))
                {
                    _hoverStateOwner = e.NewValue;
                    _isPointerOver = false;
                    _hasKeyboardFocus = false;
                }

                UpdateQuickActions();
                ConfigureCover();
            };
            PointerEntered += OnPointerEntered;
            PointerExited += OnPointerExited;
            PointerPressed += OnPointerPressed;
            PointerReleased += OnPointerReleased;
            PointerCanceled += OnPointerCanceled;
            PointerCaptureLost += OnPointerCaptureLost;
            PointerMoved += OnDragPointerMoved;
            GotFocus += OnKeyboardFocusChanged;
            LostFocus += OnKeyboardFocusChanged;

            // 拖拽启动阶段自兜底：鼠标仍在卡片上方时防止禁止图标
            AllowDrop = true;
            AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnSelfDragOver), true);
        }

        private void ConfigureCover()
        {
            if (DataContext is Anime anime)
            {
                ManagedImageLoader.ConfigureCover(
                    CoverImage,
                    anime.ID,
                    anime.CoverURL,
                    CardWidth,
                    OnCoverLoadStateChanged);
            }
            else
                ManagedImageLoader.Cancel(CoverImage);
        }

        private static void OnCardWidthChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            _ = args;
            var card = (AnimeCard)dependencyObject;
            card.ApplyCardSize();
            // 宽度变了按新尺寸重新解码；已有磁盘缓存时直接替换，不闪占位图。
            card.ConfigureCover();
        }

        private void ApplyCardSize()
        {
            var width = CardWidth > 0 ? CardWidth : AnimeCardPresentation.DefaultCardWidth;
            var height = AnimeCardPresentation.CoverHeightFor(width);
            CardRoot.Width = width;
            CoverHost.Height = height;
            CoverClip.Rect = new Rect(0, 0, width, height);
        }

        private static void OnShowSeasonDateChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            _ = args;
            ((AnimeCard)dependencyObject).UpdateAirDateText();
        }

        private void UpdateAirDateText()
        {
            var airDate = (DataContext as Anime)?.AirDate;
            AirDateText.Text = airDate is not { } date
                ? string.Empty
                : ShowSeasonDate
                    ? AnimeCardPresentation.FormatSeasonDate(date)
                    : date.ToString();
        }

        private void OnCoverLoadStateChanged(ManagedImageLoadState state)
        {
            if (RetryOverlay == null)
                return;

            RetryOverlay.Visibility = state == ManagedImageLoadState.Loaded
                ? Visibility.Collapsed
                : Visibility.Visible;
            RetryRing.Visibility = state == ManagedImageLoadState.Failed
                ? Visibility.Collapsed
                : Visibility.Visible;
            RetryRing.IsActive = state == ManagedImageLoadState.Loading;
            RetryButton.Visibility = state == ManagedImageLoadState.Failed
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnRetryCoverClick(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            ManagedImageLoader.Retry(CoverImage);
        }

        private void OnRetryCoverPointerPressed(
            object sender,
            PointerRoutedEventArgs e)
        {
            _ = sender;
            _pointerDown = false;
            _clickCandidate = false;
            e.Handled = true;
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

        private static void OnShowMediaFormatBadgeChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            _ = args;
            ((AnimeCard)dependencyObject).UpdateMediaFormatBadge();
        }

        private void UpdateMediaFormatBadge()
        {
            var text = ShowMediaFormatBadge && DataContext is Anime anime
                ? AnimeReleaseClassifier.GetMediaFormatBadgeText(anime.MediaFormat)
                : null;
            MediaFormatBadgeText.Text = text ?? string.Empty;
            MediaFormatBadge.Visibility = text is null
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void UpdateScoreBadge()
        {
            if (DataContext is Anime anime && anime.Score.HasValue && anime.Score.Value > 0)
            {
                ScoreText.Text = anime.Score.Value.ToString("F1");
                ScoreBadge.Background = AnimeCardPresentation.IsHighScore(anime.Score.Value)
                    ? _highScoreBrush
                    : (Brush)Resources["ScoreBadgeMutedBrush"];
                ScoreBadge.Visibility = Visibility.Visible;
            }
            else
            {
                ScoreBadge.Visibility = Visibility.Collapsed;
            }
        }

        private static void OnTrackingPresentationChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            _ = args;
            var card = (AnimeCard)dependencyObject;
            card.UpdateTrackingBadges();
            card.UpdateQuickActions();
        }

        private static void OnTrackingActionPendingChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            var card = (AnimeCard)dependencyObject;
            var opacity = (bool)args.NewValue ? PendingActionOpacity : 1;
            card.WatchActionButton.Opacity = opacity;
            card.FollowActionButton.Opacity = opacity;
        }

        private static void OnWeekdayTextChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            var card = (AnimeCard)dependencyObject;
            var text = args.NewValue as string;
            card.WeekdayTextBadgeText.Text = text ?? string.Empty;
            card.WeekdayTextBadge.Visibility = string.IsNullOrEmpty(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void UpdateTrackingBadges()
        {
            var status = TrackingStatus;
            WatchingBadge.Visibility = status == AnimeTrackingStatus.Watching
                ? Visibility.Visible
                : Visibility.Collapsed;
            FollowingBadge.Visibility = status == AnimeTrackingStatus.Following
                ? Visibility.Visible
                : Visibility.Collapsed;
            var showOther = status is not (AnimeTrackingStatus.None
                    or AnimeTrackingStatus.Watching
                    or AnimeTrackingStatus.Following
                    or AnimeTrackingStatus.Blocked)
                && StatusLabels.ContainsKey(status);
            OtherStatusBadgeText.Text = showOther ? StatusLabels[status] : string.Empty;
            OtherStatusBadge.Visibility = showOther
                ? Visibility.Visible
                : Visibility.Collapsed;
            CompletedDim.Visibility = status == AnimeTrackingStatus.Completed
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 与详情页一致：已是该状态时按钮表示“取消”，显示为当前状态。
            var watching = status == AnimeTrackingStatus.Watching;
            WatchActionButton.Content = watching ? "追番中 ✓" : "追番";
            WatchActionButton.Style = watching
                ? null
                : (Style)Application.Current.Resources["AccentButtonStyle"];
            FollowActionButton.Content = status == AnimeTrackingStatus.Following
                ? "关注中 ✓"
                : "关注";

            // 隐藏“追番”时，“关注”占满整行。
            WatchActionButton.Visibility = ShowWatchAction
                ? Visibility.Visible
                : Visibility.Collapsed;
            Grid.SetColumn(FollowActionButton, ShowWatchAction ? 1 : 0);
            Grid.SetColumnSpan(FollowActionButton, ShowWatchAction ? 1 : 2);
        }

        /// <summary>悬停时显示标记按钮并暂时隐藏评分，避免两者重叠。</summary>
        private void UpdateQuickActions()
        {
            IsTabStop = ShowQuickActions;
            var show = ShowQuickActions
                && (_isPointerOver || _hasKeyboardFocus)
                && DataContext is Anime;
            QuickActions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                ScoreBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateScoreBadge();
            }
        }

        private void OnKeyboardFocusChanged(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            DispatcherQueue.TryEnqueue(() =>
            {
                _hasKeyboardFocus = ContainsKeyboardFocus();
                UpdateQuickActions();
            });
        }

        /// <summary>
        /// 只认键盘焦点：鼠标点击标记按钮也会让按钮获得焦点，
        /// 若同样计入，移开鼠标后按钮浮层会一直留着。
        /// </summary>
        private bool ContainsKeyboardFocus()
        {
            if (XamlRoot is null
                || FocusManager.GetFocusedElement(XamlRoot)
                    is not Control { FocusState: FocusState.Keyboard } focused)
            {
                return false;
            }

            DependencyObject? current = focused;
            while (current is not null)
            {
                if (ReferenceEquals(current, this))
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private void OnWatchActionClick(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            RequestTrackingAction(AnimeTrackingStatus.Watching);
        }

        private void OnFollowActionClick(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            RequestTrackingAction(AnimeTrackingStatus.Following);
        }

        private void RequestTrackingAction(AnimeTrackingStatus status)
        {
            _clickCandidate = false;
            if (IsTrackingActionPending)
            {
                return;
            }

            if (DataContext is Anime anime)
            {
                TrackingActionRequested?.Invoke(
                    this,
                    new AnimeCardTrackingActionEventArgs(anime, status));
            }
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = true;
            UpdateQuickActions();

            var visual = ElementCompositionPreview.GetElementVisual(this);
            visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 16));

            AnimateScale(1.05, 200);
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = false;
            UpdateQuickActions();

            var visual = ElementCompositionPreview.GetElementVisual(this);
            visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 0));

            AnimateScale(1.0, 200);
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _pointerDown = true;
            _clickCandidate = true;
            _standardDragStarted = false;
            _pointerDownPoint = e.GetCurrentPoint(this).Position;

            AnimateScale(0.95, 100);
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

            AnimateScale(1.05, 100);
        }

        /// <summary>
        /// 用 XAML 缩放变换做悬停/按下缩放。合成动画的缩放只拉伸已渲染的画面，
        /// 文字会发虚；依赖动画每帧按当前比例重新渲染，文字保持清晰。
        /// 新动画从当前值接续，不先停止旧动画，避免跳回原始大小。
        /// </summary>
        private void AnimateScale(double to, int milliseconds)
        {
            var storyboard = new Storyboard();
            foreach (var property in new[] { nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY) })
            {
                var animation = new DoubleAnimation
                {
                    To = to,
                    Duration = TimeSpan.FromMilliseconds(milliseconds),
                    EnableDependentAnimation = true,
                };
                Storyboard.SetTarget(animation, _cardScale);
                Storyboard.SetTargetProperty(animation, property);
                storyboard.Children.Add(animation);
            }

            storyboard.Begin();
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
