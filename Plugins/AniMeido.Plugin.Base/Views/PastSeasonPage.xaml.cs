using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class PastSeasonPage : Page, INavigationAware
    {
        private const int EarliestSupportedYear = 1900;
        // 连点季度箭头时，停下这么久才加载，只加载最后停下的那一季。
        private static readonly TimeSpan SeasonLoadDelay = TimeSpan.FromMilliseconds(250);
        // 加载超过这么久才显示骨架，本地数据秒开时不闪一下。
        private static readonly TimeSpan SkeletonDelay = TimeSpan.FromMilliseconds(150);
        // 拖动窗口边缘时合并卡片宽度的重算。
        private static readonly TimeSpan GridLayoutDelay = TimeSpan.FromMilliseconds(100);
        // 工具栏窄于这个宽度时排成两行（一行需要放下六个形态标签和全部控件）。
        private const double ToolbarSingleRowWidth = 1480;
        // 卡片外边距（左右各 6）与列表项右边距 4。
        private const double CardSlotExtra = 16;
        // 卡片封面以下的高度：文字区、边框与上下外边距，用于骨架占位。
        private const double CardBelowCoverHeight = 96;
        private const int SkeletonCount = 18;
        private const VirtualKey PreviousSeasonKey = (VirtualKey)219; // [
        private const VirtualKey NextSeasonKey = (VirtualKey)221;     // ]

        private static readonly PastSeasonTarget EarliestSeason = new(EarliestSupportedYear, Season.Winter);

        private readonly PastSeasonViewModel _viewModel;
        public PastSeasonViewModel ViewModel => _viewModel;
        private readonly CacheService _cacheService;
        private readonly DragDropService _dragDrop;
        private readonly IPluginNavigator _pluginNavigator;
        private readonly ILogger<PastSeasonPage> _logger;
        private readonly DispatcherQueueTimer _seasonLoadTimer;
        private readonly DispatcherQueueTimer _skeletonTimer;
        private readonly DispatcherQueueTimer _gridLayoutTimer;
        private CancellationTokenSource? _loadCts;
        private int _loadVersion;
        private PastSeasonTarget _selected;
        private int _flyoutYear;
        private double _cardWidth = AnimeCardPresentation.DefaultCardWidth;
        private bool? _toolbarIsSingleRow;
        private bool _isSyncingControls;

        public PastSeasonPage(
            IAnimeDataSource dataSource,
            CacheService cacheService,
            DragDropService dragDropService,
            TrackingService trackingService,
            IPluginNavigator pluginNavigator,
            ILogger<PastSeasonPage> logger)
        {
            _viewModel = new PastSeasonViewModel(dataSource, trackingService);
            _cacheService = cacheService;
            _dragDrop = dragDropService;
            _pluginNavigator = pluginNavigator;
            _logger = logger;
            InitializeComponent();

            _seasonLoadTimer = CreateTimer(SeasonLoadDelay, () => _ = LoadSelectedSeasonSafelyAsync());
            _skeletonTimer = CreateTimer(SkeletonDelay, ShowSkeletonIfLoading);
            _gridLayoutTimer = CreateTimer(GridLayoutDelay, ApplyCardWidth);

            _selected = LatestSeason();
            SkeletonRepeater.ItemsSource = Enumerable.Range(0, SkeletonCount).ToArray();
            InitializeSortControls();
            UpdateSeasonControls();

            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(PastSeasonViewModel.IsLoading)
                    or nameof(PastSeasonViewModel.IsError))
                {
                    UpdateSkeleton();
                }
            };
        }

        private DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = interval;
            timer.IsRepeating = false;
            timer.Tick += (s, e) => tick();
            return timer;
        }

        /// <summary>
        /// 从放送日历的番剧时光机进入时，直接定位到指定季度；
        /// 从主导航进入时没有参数，保持当前季度（首次为最近一个已完结季度）。
        /// </summary>
        public async Task OnNavigatedToAsync(object? parameter)
        {
            if (parameter is not PastSeasonTarget target
                || !PastSeasonBrowse.IsWithin(target, EarliestSeason, LatestSeason()))
            {
                return;
            }

            _selected = target;
            UpdateSeasonControls();
            await LoadSelectedSeasonSafelyAsync();
        }

        internal static (int Year, Season Season) GetLatestCompletedSeason(
            DateTime now)
        {
            return now.Month switch
            {
                >= 1 and <= 3 => (now.Year - 1, Season.Fall),
                >= 4 and <= 6 => (now.Year, Season.Winter),
                >= 7 and <= 9 => (now.Year, Season.Spring),
                _ => (now.Year, Season.Summer),
            };
        }

        private static PastSeasonTarget LatestSeason()
        {
            var (year, season) = GetLatestCompletedSeason(DateTime.Now);
            return new PastSeasonTarget(year, season);
        }

        // ======== 季度切换 ========

        private void OnPrevSeasonClick(object sender, RoutedEventArgs e) => StepSeason(-1);

        private void OnNextSeasonClick(object sender, RoutedEventArgs e) => StepSeason(1);

        /// <summary>逐季切换：标签立即更新，停下片刻后才加载。</summary>
        private void StepSeason(int delta)
        {
            if (PastSeasonBrowse.Step(_selected, delta, EarliestSeason, LatestSeason()) is not { } next)
                return;

            _selected = next;
            UpdateSeasonControls();
            _seasonLoadTimer.Stop();
            _seasonLoadTimer.Start();
        }

        /// <summary>从面板选定季度：立即加载。</summary>
        private void SelectSeason(PastSeasonTarget target)
        {
            SeasonFlyout.Hide();
            _selected = PastSeasonBrowse.Clamp(target, EarliestSeason, LatestSeason());
            UpdateSeasonControls();
            _ = LoadSelectedSeasonSafelyAsync();
        }

        private void UpdateSeasonControls()
        {
            var latest = LatestSeason();
            SeasonLabel.Text = $"{_selected.Year} {PastSeasonBrowse.SeasonName(_selected.Season)}";
            SeasonMonthsLabel.Text = PastSeasonBrowse.SeasonMonths(_selected.Season);
            PrevSeasonButton.IsEnabled = PastSeasonBrowse.Step(_selected, -1, EarliestSeason, latest) is not null;
            NextSeasonButton.IsEnabled = PastSeasonBrowse.Step(_selected, 1, EarliestSeason, latest) is not null;
        }

        private void OnSeasonFlyoutOpening(object sender, object e)
        {
            var latest = LatestSeason();
            FlyoutYearBox.Minimum = EarliestSupportedYear;
            FlyoutYearBox.Maximum = latest.Year;
            SetFlyoutYear(_selected.Year);
        }

        private void SetFlyoutYear(int year)
        {
            var latest = LatestSeason();
            _flyoutYear = Math.Clamp(year, EarliestSupportedYear, latest.Year);
            _isSyncingControls = true;
            FlyoutYearBox.Value = _flyoutYear;
            _isSyncingControls = false;
            FlyoutPrevYearButton.IsEnabled = _flyoutYear > EarliestSupportedYear;
            FlyoutNextYearButton.IsEnabled = _flyoutYear < latest.Year;
            RebuildFlyoutSeasonButtons(latest);
        }

        private void RebuildFlyoutSeasonButtons(PastSeasonTarget latest)
        {
            FlyoutSeasonGrid.Children.Clear();
            foreach (var season in new[] { Season.Winter, Season.Spring, Season.Summer, Season.Fall })
            {
                var target = new PastSeasonTarget(_flyoutYear, season);
                var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new TextBlock
                {
                    Text = PastSeasonBrowse.SeasonName(season),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                content.Children.Add(new TextBlock
                {
                    Text = PastSeasonBrowse.SeasonMonths(season),
                    FontSize = 11,
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                var button = new Button
                {
                    Content = content,
                    Tag = target,
                    IsEnabled = PastSeasonBrowse.IsWithin(target, EarliestSeason, latest),
                    Style = (Style)(target == _selected
                        ? Application.Current.Resources["AccentButtonStyle"]
                        : Resources["SeasonPickButtonStyle"]),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0, 6, 0, 6),
                };
                button.Click += OnFlyoutSeasonClick;
                Grid.SetColumn(button, (int)season - 1);
                FlyoutSeasonGrid.Children.Add(button);
            }
        }

        private void OnFlyoutSeasonClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: PastSeasonTarget target })
                SelectSeason(target);
        }

        private void OnFlyoutPrevYearClick(object sender, RoutedEventArgs e) => SetFlyoutYear(_flyoutYear - 1);

        private void OnFlyoutNextYearClick(object sender, RoutedEventArgs e) => SetFlyoutYear(_flyoutYear + 1);

        private void OnFlyoutYearChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isSyncingControls || double.IsNaN(args.NewValue))
                return;

            SetFlyoutYear((int)Math.Round(args.NewValue));
        }

        private void OnLatestSeasonClick(object sender, RoutedEventArgs e) => SelectSeason(LatestSeason());

        // ======== 加载 ========

        private Task LoadSelectedSeasonSafelyAsync(bool forceRefresh = false)
            => LoadSeasonSafelyAsync(_selected, forceRefresh);

        private async Task LoadSeasonAsync(PastSeasonTarget target, bool forceRefresh)
        {
            _seasonLoadTimer.Stop();

            // 取消上一轮请求
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            var loadCts = new CancellationTokenSource();
            _loadCts = loadCts;
            var version = Interlocked.Increment(ref _loadVersion);
            ErrorInfoBar.IsOpen = false;

            // 换季度时清空搜索词；形态、排序与“隐藏看过”保留。
            if (ViewModel.Season != target && !string.IsNullOrEmpty(FilterBox.Text))
            {
                _isSyncingControls = true;
                FilterBox.Text = "";
                _isSyncingControls = false;
            }

            if (forceRefresh)
            {
                await _cacheService.RemoveCacheAsync(
                    BangumiDataSource.GetSeasonCacheKey(target.Year, target.Season));
                if (version != _loadVersion)
                    return;
            }

            await ViewModel.LoadPastSeasonAnimeAsync(target.Year, target.Season, loadCts.Token);
        }

        private async Task LoadSeasonSafelyAsync(
            PastSeasonTarget target,
            bool forceRefresh = false)
        {
            try
            {
                await LoadSeasonAsync(target, forceRefresh);
            }
            catch (OperationCanceledException)
            {
            }
#pragma warning disable CA1031 // UI 事件边界统一显示加载失败，避免 async void 终止进程。
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Library season {Year}/{Season} failed to load", target.Year, target.Season);
                ErrorInfoBar.Message = $"加载失败：{ex.Message}";
                ErrorInfoBar.IsOpen = true;
            }
#pragma warning restore CA1031
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
            => _ = LoadSelectedSeasonSafelyAsync(forceRefresh: true);

        private void OnRetryClick(object sender, RoutedEventArgs e)
            => _ = LoadSelectedSeasonSafelyAsync();

        private void UpdateSkeleton()
        {
            if (ViewModel.IsLoading)
            {
                _skeletonTimer.Stop();
                _skeletonTimer.Start();
            }
            else
            {
                _skeletonTimer.Stop();
                SkeletonRepeater.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowSkeletonIfLoading()
        {
            if (ViewModel.IsLoading)
                SkeletonRepeater.Visibility = Visibility.Visible;
        }

        private void OnAnimeCardClicked(object? sender, AnimeCardClickedEventArgs e)
        {
            _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);
        }

        // ======== 筛选与排序 ========

        private void InitializeSortControls()
        {
            foreach (var (key, label) in new[]
            {
                (PastSeasonSortKey.Score, "评分"),
                (PastSeasonSortKey.AirDate, "开播日期"),
                (PastSeasonSortKey.Title, "标题"),
            })
            {
                SortComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            }

            SortComboBox.SelectedIndex = 0;
            SortComboBox.SelectionChanged += OnSortSelectionChanged;
            UpdateSortDirectionButton();
        }

        private void OnSortSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SortComboBox.SelectedItem is ComboBoxItem { Tag: PastSeasonSortKey key })
            {
                ViewModel.SetSortKey(key);
                UpdateSortDirectionButton();
            }
        }

        private void OnSortDirectionClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ToggleSortDirection();
            UpdateSortDirectionButton();
        }

        private void UpdateSortDirectionButton()
        {
            SortDirectionIcon.Glyph = ViewModel.SortAscending ? "\uE74A" : "\uE74B";
            var text = PastSeasonBrowse.SortDirectionText(ViewModel.SortKey, ViewModel.SortAscending);
            ToolTipService.SetToolTip(SortDirectionButton, text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SortDirectionButton, text);
        }

        private void OnFormatChipClick(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton { Tag: PastSeasonFormatChip chip } button)
                return;

            // 再点已选中的标签不取消选中，保持“始终选中一项”。
            if (chip.IsSelected)
            {
                button.IsChecked = true;
                return;
            }

            ViewModel.SelectFormat(chip.Format);
        }

        private void OnHideCompletedToggled(object sender, RoutedEventArgs e)
        {
            if (!_isSyncingControls)
                ViewModel.SetHideCompleted(HideCompletedSwitch.IsOn);
        }

        private void OnFilterTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (!_isSyncingControls)
                ViewModel.SetQuery(sender.Text);
        }

        private void OnClearFiltersClick(object sender, RoutedEventArgs e)
        {
            _isSyncingControls = true;
            FilterBox.Text = "";
            HideCompletedSwitch.IsOn = false;
            _isSyncingControls = false;
            ViewModel.ClearFilters();
        }

        // ======== 键盘 ========

        /// <summary>[ ] 逐季切换；焦点在输入框里时让给文字输入。</summary>
        private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key is not (PreviousSeasonKey or NextSeasonKey) || IsTextInputFocused())
                return;

            StepSeason(e.Key == PreviousSeasonKey ? -1 : 1);
            e.Handled = true;
        }

        private bool IsTextInputFocused()
            => XamlRoot is not null
                && FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox or NumberBox;

        private void OnFindAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            FilterBox.Focus(FocusState.Keyboard);
            args.Handled = true;
        }

        // ======== 布局 ========

        /// <summary>工具栏宽时一行，窄时季度与形态一行、其余一行。</summary>
        private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var singleRow = e.NewSize.Width >= ToolbarSingleRowWidth;
            if (_toolbarIsSingleRow == singleRow)
                return;

            _toolbarIsSingleRow = singleRow;
            // 第二行为空时行间距仍会占位，单行时去掉。
            ToolbarGrid.RowSpacing = singleRow ? 0 : 10;
            Grid.SetRow(SecondaryTools, singleRow ? 0 : 1);
            Grid.SetColumn(SecondaryTools, singleRow ? 1 : 0);
            Grid.SetColumnSpan(SecondaryTools, singleRow ? 1 : 2);
            Grid.SetColumnSpan(PrimaryTools, singleRow ? 1 : 2);
            FilterBox.Width = singleRow ? 220 : double.NaN;
        }

        private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _gridLayoutTimer.Stop();
            _gridLayoutTimer.Start();
        }

        /// <summary>按网格宽度算出每行几张、每张多宽，让卡片铺满整行。</summary>
        private void ApplyCardWidth()
        {
            var available = AnimeGridView.ActualWidth
                - AnimeGridView.Padding.Left
                - AnimeGridView.Padding.Right;
            if (available <= 0)
                return;

            var slot = AnimeCardPresentation.DefaultCardWidth + CardSlotExtra;
            var columns = Math.Max(1, (int)Math.Floor(available / slot));
            var width = Math.Max(
                AnimeCardPresentation.DefaultCardWidth,
                Math.Floor(available / columns) - CardSlotExtra);
            if (Math.Abs(width - _cardWidth) < 0.5
                && AnimeGridView.ItemsPanelRoot is ItemsWrapGrid { ItemWidth: > 0 })
            {
                return;
            }

            _cardWidth = width;
            if (AnimeGridView.ItemsPanelRoot is ItemsWrapGrid panel)
            {
                panel.ItemWidth = width + CardSlotExtra;
                foreach (var child in panel.Children)
                {
                    if (child is GridViewItem { ContentTemplateRoot: AnimeCard card })
                        card.CardWidth = width;
                }

                panel.InvalidateMeasure();
            }

            SkeletonLayout.MinItemWidth = width + CardSlotExtra;
            SkeletonLayout.MinItemHeight = AnimeCardPresentation.CoverHeightFor(width) + CardBelowCoverHeight;
        }

        private void OnGridContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer?.ContentTemplateRoot is AnimeCard card && card.CardWidth != _cardWidth)
                card.CardWidth = _cardWidth;
        }

        // ======== 生命周期与自定义拖放 ========

        private IDisposable? _dropHostRegistration;

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _dropHostRegistration?.Dispose();
            _dropHostRegistration = _dragDrop.AttachStandardDragHost(
                RootGrid,
                DragOverlay,
                DragAction.PlanToWatch);

            // 确保 Unloaded 只注册一次
            RootGrid.Unloaded -= OnRootGridUnloaded;
            RootGrid.Unloaded += OnRootGridUnloaded;

            // 返回已缓存页面时重新读取追番状态：刷新卡片标签，移除刚屏蔽的条目。
            _ = LoadDragConfigAndStatusesAsync();
            // 首次进入、上次加载失败或被离开打断，或离开前刚切了季度还没加载时，加载所选季度。
            if (!ViewModel.IsLoading
                && (ViewModel.Season != _selected || ViewModel.LoadedAnime.Count == 0))
            {
                _ = LoadSelectedSeasonSafelyAsync();
            }
        }

        private async Task LoadDragConfigAndStatusesAsync()
        {
            try
            {
                await _dragDrop.ReloadConfigAsync();
                if (!await ViewModel.ReloadStatusesAsync())
                    _logger.LogWarning("Library tracking statuses could not be read; keeping the previous marks");
            }
#pragma warning disable CA1031 // 拖放配置与状态读取失败不阻塞页面
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Library drag config or tracking statuses failed to load");
            }
#pragma warning restore CA1031
        }

        private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
        {
            _dropHostRegistration?.Dispose();
            _dropHostRegistration = null;
            _seasonLoadTimer.Stop();
            _skeletonTimer.Stop();
            _gridLayoutTimer.Stop();
            Interlocked.Increment(ref _loadVersion);
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
        }
    }
}
