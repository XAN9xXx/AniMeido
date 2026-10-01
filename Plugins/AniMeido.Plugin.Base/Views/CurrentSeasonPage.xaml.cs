using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class CurrentSeasonPage : Page
    {
        // 滚动位置可能带小数，判断是否到头时留一点容差，避免停在边界附近仍显示渐隐。
        private const double EdgeTolerance = 2;
        // 封面宽高比，与原来 38×52 的缩略图一致。
        private const double PickCoverAspect = 38.0 / 52;
        private const double DailyPickCoverAspect = 120.0 / 168;
        // 右侧文字各部分的大致高度，用来决定收起哪些内容。
        private const double DailyPickLineHeight = 19;
        private const double DailyPickTitleLineHeight = 22;
        private const double DailyPickMetaHeight = 16;
        private const double DailyPickTagsHeight = 20;
        private const double DailyPickButtonsHeight = 34;
        private const double DailyPickSpacing = 6;

        public CurrentSeasonViewModel ViewModel { get; }

        private readonly DragDropService _dragDrop;
        private readonly IPluginNavigator _pluginNavigator;
        private readonly HashSet<FrameworkElement> _hoveredPickRows = [];
        private IDisposable? _dropHostRegistration;
        private double _wheelTarget = double.NaN;
        private bool _applyingLowerLayout;

        public CurrentSeasonPage(IAnimeDataSource dataSource, DragDropService dragDropService, TrackingService trackingService, IPluginNavigator pluginNavigator)
        {
            ViewModel = new CurrentSeasonViewModel(dataSource, trackingService);
            _dragDrop = dragDropService;
            _pluginNavigator = pluginNavigator;
            InitializeComponent();

            ViewModel.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(CurrentSeasonViewModel.IsLoading):
                    case nameof(CurrentSeasonViewModel.IsError):
                    case nameof(CurrentSeasonViewModel.ErrorMessage):
                        UpdateOverlayState();
                        break;

                    case nameof(CurrentSeasonViewModel.IsFiltering):
                        ApplyFilterLayout();
                        break;

                    case nameof(CurrentSeasonViewModel.SearchText):
                        // 点星期格跳转时由 ViewModel 清空搜索，输入框需要跟着清空。
                        if (FilterBox.Text != ViewModel.SearchText)
                            FilterBox.Text = ViewModel.SearchText;
                        break;

                    case nameof(CurrentSeasonViewModel.ShowMineOnly):
                        UpdateScopeButtons();
                        break;

                    case nameof(CurrentSeasonViewModel.VisibleEntries):
                        // 换了一天或换了结果：回到开头。
                        _wheelTarget = double.NaN;
                        ShelfScroller.ChangeView(0, null, null, disableAnimation: true);
                        ResultsScroller.ChangeView(null, 0, null, disableAnimation: true);
                        UpdateShelfFades();
                        break;

                    case nameof(CurrentSeasonViewModel.TimeMachineYearsAgo):
                        UpdateTimeMachineYearButtons();
                        break;

                    case nameof(CurrentSeasonViewModel.DailyPick):
                        UpdateDailyPickCover();
                        ApplyLowerLayout();
                        break;

                    case nameof(CurrentSeasonViewModel.DailyPickTags):
                        ApplyLowerLayout();
                        break;

                    case nameof(CurrentSeasonViewModel.IsDailyPickWatching):
                        // 与卡片一致：已在追番时按钮表示“取消”，不再用强调色。
                        DailyPickWatchButton.Style = ViewModel.IsDailyPickWatching
                            ? null
                            : (Style)Application.Current.Resources["AccentButtonStyle"];
                        break;
                }
            };

            // 拖放覆盖层关闭说明一次拖放结束，可能刚写入了标记。
            DragOverlay.RegisterPropertyChangedCallback(
                VisibilityProperty,
                OnDragOverlayVisibilityChanged);

            ViewModel.LoadSeasonalAnimeCommand.Execute(null);
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid rootGrid)
                return;

            _dropHostRegistration?.Dispose();
            _dropHostRegistration = _dragDrop.AttachStandardDragHost(
                rootGrid,
                DragOverlay,
                DragAction.PlanToWatch);

            // 确保 Unloaded 只注册一次
            rootGrid.Unloaded -= OnRootGridUnloaded;
            rootGrid.Unloaded += OnRootGridUnloaded;

            // 返回已缓存页面时重新读取拖放配置与本地标记（可能在详情页改过状态或屏蔽）。
            _ = ReloadDragConfigAndStatusesAsync();

            if (!ViewModel.LoadSeasonalAnimeCommand.IsRunning
                && !ViewModel.HasData
                && !ViewModel.IsError)
            {
                ViewModel.LoadSeasonalAnimeCommand.Execute(null);
            }

            // 从详情页返回时复用的是同一个页面，离开时取消的补充请求在这里补上。
            ViewModel.ResumeSupplementaryLoads();

            ApplyFilterLayout();
            // 年份在应用运行期间保留，重新打开页面时按钮要跟着对上。
            UpdateTimeMachineYearButtons();
        }

        private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.LoadSeasonalAnimeCommand.Cancel();
            ViewModel.CancelSupplementaryLoads();
            _hoveredPickRows.Clear();
            _dropHostRegistration?.Dispose();
            _dropHostRegistration = null;
        }

        private async Task ReloadDragConfigAndStatusesAsync()
        {
            try
            {
                await _dragDrop.ReloadConfigAsync();
                await ViewModel.ReloadStatusesAsync();
            }
#pragma warning disable CA1031 // 拖放配置或标记刷新失败不阻塞页面
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CurrentSeasonPage] ReloadDragConfigAndStatusesAsync failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        private void OnDragOverlayVisibilityChanged(DependencyObject sender, DependencyProperty property)
        {
            if (DragOverlay.Visibility == Visibility.Collapsed)
            {
                _ = ReloadDragConfigAndStatusesAsync();
            }
        }

        private void UpdateOverlayState()
        {
            ErrorInfoBar.Message = ViewModel.ErrorMessage;
            ErrorInfoBar.IsOpen = ViewModel.IsError;

            bool showOverlay = ViewModel.IsLoading || ViewModel.IsError;
            LoadingOverlay.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
            LoadingRing.IsActive = ViewModel.IsLoading;

            if (ViewModel.IsError)
            {
                LoadingFailedImage.Visibility = Visibility.Visible;
                LoadingRing.Visibility = Visibility.Collapsed;
                LoadingHint.Text = $"{ViewModel.ErrorMessage}\n\n点击重试";
            }
            else if (ViewModel.IsLoading)
            {
                LoadingFailedImage.Visibility = Visibility.Collapsed;
                LoadingRing.Visibility = Visibility.Visible;
                LoadingHint.Text = "加载中…";
            }
            else
            {
                LoadingFailedImage.Visibility = Visibility.Collapsed;
                LoadingHint.Text = "";
            }
        }

        private void OnLoadingOverlayTapped(object sender, TappedRoutedEventArgs e)
        {
            if (ViewModel.IsError)
            {
                ViewModel.RetryLoadCommand.Execute(null);
            }
        }

        // ======== 布局 ========

        /// <summary>
        /// 按天：先紧凑显示下方两块，再由正文滚动保留它们。
        /// 搜索或只看“我的”：只在结果网格内滚动，避免内外两层纵向滚动。
        /// </summary>
        private void ApplyFilterLayout()
        {
            var filtering = ViewModel.IsFiltering;
            CalendarBodyGrid.RowDefinitions[0].Height = filtering
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
            CalendarBodyGrid.RowDefinitions[1].Height = filtering
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
            DiscoverHost.Visibility = filtering ? Visibility.Collapsed : Visibility.Visible;
            ShelfHost.Visibility = filtering ? Visibility.Collapsed : Visibility.Visible;
            ResultsScroller.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;
            BodyScroller.VerticalScrollMode = filtering ? ScrollMode.Disabled : ScrollMode.Enabled;
            BodyScroller.VerticalScrollBarVisibility = filtering ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            BodyScroller.ChangeView(null, 0, null, disableAnimation: true);
            ApplyLowerLayout();
            UpdateScopeButtons();
        }

        private void OnBodyViewportSizeChanged(object sender, SizeChangedEventArgs e)
            => ApplyLowerLayout();

        private void OnMainPanelSizeChanged(object sender, SizeChangedEventArgs e)
            => ApplyLowerLayout();

        private void OnDiscoverHostSizeChanged(object sender, SizeChangedEventArgs e)
            => ApplyLowerLayout();

        /// <summary>
        /// 下方区域始终保留最少内容；窄时上下排列，矮时增加正文高度以便滚动访问。
        /// 显式约束正文高度，避免滚动容器的无限测量让星号行与列表容量互相撑大。
        /// </summary>
        private void ApplyLowerLayout()
        {
            if (_applyingLowerLayout || BodyScroller.ActualHeight <= 0 || CalendarBodyGrid.ActualWidth <= 0)
                return;

            _applyingLowerLayout = true;
            try
            {
                var viewportHeight = BodyScroller.ActualHeight;
                if (ViewModel.IsFiltering)
                {
                    CalendarBodyGrid.Height = viewportHeight;
                    ViewModel.SetDiscoverCapacity(0);
                    return;
                }

                var mainHeight = MainPanel.ActualHeight + MainPanel.Margin.Top + MainPanel.Margin.Bottom;
                var lowerMargin = DiscoverHost.Margin.Top + DiscoverHost.Margin.Bottom;
                var layout = CalendarLowerLayout.Calculate(CalendarBodyGrid.ActualWidth,
                    viewportHeight - mainHeight - lowerMargin, ViewModel.HasDailyPick);
                CalendarBodyGrid.Height = Math.Max(viewportHeight, mainHeight + lowerMargin + layout.Height);
                DiscoverHost.Height = layout.Height;
                DiscoverHost.RowDefinitions[0].Height = layout.StackPanels ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
                DiscoverHost.RowDefinitions[1].Height = layout.StackPanels ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                DiscoverHost.ColumnDefinitions[0].Width = layout.StackPanels ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
                DiscoverHost.ColumnDefinitions[1].Width = layout.StackPanels ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                DiscoverHost.RowSpacing = layout.StackPanels ? CalendarLowerLayout.PanelSpacing : 0;

                // 只有确实没有候选作品时才不显示今日一抽，不再按窗口尺寸隐藏。
                DailyPickPanel.Visibility = ViewModel.HasDailyPick ? Visibility.Visible : Visibility.Collapsed;
                DailyPickPanel.Width = layout.StackPanels ? double.NaN : CalendarLowerLayout.DailyPickWidth;
                DailyPickPanel.Height = layout.DailyPickHeight;
                DailyPickPanel.Margin = layout.StackPanels ? new Thickness(0) : new Thickness(0, 0, CalendarLowerLayout.PanelSpacing, 0);
                if (ViewModel.HasDailyPick)
                    FitDailyPick(layout.DailyPickHeight - CalendarLowerLayout.DailyPickChromeHeight);

                Grid.SetRow(DiscoverPanel, layout.StackPanels ? 1 : 0);
                Grid.SetColumn(DiscoverPanel, layout.StackPanels ? 0 : 1);
                DiscoverPanel.Height = layout.DiscoverHeight;
                Grid.SetRow(TimeMachineHeaderActions, layout.CompactTimeMachineHeader ? 1 : 0);
                Grid.SetColumn(TimeMachineHeaderActions, layout.CompactTimeMachineHeader ? 0 : 1);
                Grid.SetColumnSpan(TimeMachineHeaderActions, layout.CompactTimeMachineHeader ? 2 : 1);
                TimeMachineHeader.RowSpacing = layout.CompactTimeMachineHeader ? 8 : 0;
                TimeMachineHeaderActions.HorizontalAlignment = layout.CompactTimeMachineHeader ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                TimeMachineHeaderActions.Orientation = layout.StackTimeMachineActions ? Orientation.Vertical : Orientation.Horizontal;
                TimeMachineHeaderActions.Spacing = layout.StackTimeMachineActions ? 4 : 12;

                ViewModel.SetDiscoverCapacity(layout.Rows * layout.Columns);
                TimeMachineLayout.MinItemWidth = Math.Min(CalendarLowerLayout.PickMinWidth,
                    Math.Max(0, layout.DiscoverWidth - CalendarLowerLayout.DiscoverChromeWidth));
                var listHeight = layout.DiscoverHeight - layout.DiscoverChromeHeight;
                TimeMachineLayout.MinItemHeight = Math.Min(
                    CalendarLowerLayout.PickMaxHeight,
                    Math.Floor((listHeight - CalendarLowerLayout.PickRowSpacing * (layout.Rows - 1)) / layout.Rows));
            }
            finally
            {
                _applyingLowerLayout = false;
            }
        }

        /// <summary>
        /// 按可用高度缩放封面，右侧与封面等高。矮时先把标题压成一行，再藏标签，
        /// 简介占剩下的空间（行数见 <see cref="OnDailyPickDescriptionHostSizeChanged"/>）；
        /// “追番 / 关注”始终保留。
        /// </summary>
        private void FitDailyPick(double available)
        {
            DailyPickCoverButton.Height = available;
            DailyPickCoverButton.Width = Math.Round(available * DailyPickCoverAspect);
            DailyPickText.Height = available;

            var titleLines = available >= 150 ? 2 : 1;
            DailyPickTitle.MaxLines = titleLines;
            var used = titleLines * DailyPickTitleLineHeight
                + DailyPickMetaHeight
                + DailyPickButtonsHeight
                + DailyPickSpacing * 3;

            // 有标签、且放下标签后简介至少还能有一行，才显示标签。
            var showTags = ViewModel.HasDailyPickTags
                && available - used >= DailyPickTagsHeight + DailyPickSpacing + DailyPickLineHeight;
            DailyPickTagsHost.Visibility = showTags ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>简介按所在格子的实际高度决定行数，放不下一行时隐藏。</summary>
        private void OnDailyPickDescriptionHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var lines = (int)Math.Floor(e.NewSize.Height / DailyPickLineHeight);
            DailyPickDescriptionHost.Opacity = lines > 0 ? 1 : 0;
            DailyPickDescription.MaxLines = Math.Max(1, lines);
        }

        private void UpdateDailyPickCover()
        {
            if (ViewModel.DailyPick?.Anime is { } anime)
                ManagedImageLoader.ConfigureCover(DailyPickCover, anime.ID, anime.CoverURL, 120);
            else
                ManagedImageLoader.Cancel(DailyPickCover);
        }

        private void OnDailyPickOpenClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.DailyPick?.Anime is { } anime)
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), anime.ID);
        }

        private async void OnDailyPickWatchClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.DailyPick?.Anime is { } anime)
                await ToggleStatusSafelyAsync(anime.ID, AnimeTrackingStatus.Watching);
        }

        private async void OnDailyPickFollowClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.DailyPick?.Anime is { } anime)
                await ToggleStatusSafelyAsync(anime.ID, AnimeTrackingStatus.Following);
        }


        // ======== 横向列表 ========

        private void OnShelfViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!e.IsIntermediate)
            {
                _wheelTarget = double.NaN;
            }

            UpdateShelfFades();
        }

        private void OnShelfSizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateShelfFades();

        private void UpdateShelfFades()
        {
            var offset = ShelfScroller.HorizontalOffset;
            ShelfStartFade.Opacity = offset > EdgeTolerance ? 1 : 0;
            ShelfEndFade.Opacity =
                ShelfScroller.ScrollableWidth - offset > EdgeTolerance ? 1 : 0;
        }

        /// <summary>
        /// 滚轮上下转为左右，按滚动量连续滚动。只有滚动容器自己没有处理滚轮时才会到这里。
        /// </summary>
        private void OnShelfPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(ShelfScroller).Properties;
            if (properties.IsHorizontalMouseWheel
                || properties.MouseWheelDelta == 0
                || ShelfScroller.ScrollableWidth <= 0)
            {
                return;
            }

            var from = double.IsNaN(_wheelTarget)
                ? ShelfScroller.HorizontalOffset
                : _wheelTarget;
            _wheelTarget = Math.Clamp(
                from - properties.MouseWheelDelta,
                0,
                ShelfScroller.ScrollableWidth);
            ShelfScroller.ChangeView(_wheelTarget, null, null, disableAnimation: false);
            e.Handled = true;
        }

        // ======== 工具栏 ========

        private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.SearchText = FilterBox.Text;
        }

        private void OnAllScopeClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ShowMineOnly = false;
            UpdateScopeButtons();
        }

        private void OnMineScopeClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ShowMineOnly = true;
            UpdateScopeButtons();
        }

        /// <summary>两个范围按钮互斥；点击已选中的按钮也保持选中。</summary>
        private void UpdateScopeButtons()
        {
            AllScopeButton.IsChecked = !ViewModel.ShowMineOnly;
            MineScopeButton.IsChecked = ViewModel.ShowMineOnly;
        }

        private void OnSortSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.Sort = SortBox.SelectedIndex switch
            {
                1 => CalendarSort.AirDate,
                2 => CalendarSort.Title,
                _ => CalendarSort.Score,
            };
        }

        private void OnDayClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: CalendarDay day })
            {
                ViewModel.SelectDay(day.Weekday);
            }
        }

        // ======== 卡片与标记 ========

        private void OnAnimeCardClicked(object? sender, AnimeCardClickedEventArgs e)
        {
            _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);
        }

        private async void OnTrackingActionRequested(
            object? sender,
            AnimeCardTrackingActionEventArgs e)
            => await ToggleStatusSafelyAsync(e.Anime.ID, e.Status);

        private void OnPickClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Anime anime })
            {
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), anime.ID);
            }
        }

        // ======== 番剧时光机 ========

        private async void OnTimeMachinePlanClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Anime anime })
                await ToggleTimeMachineStatusSafelyAsync(anime.ID, AnimeTrackingStatus.PlanToWatch);
        }

        private async void OnTimeMachineFollowClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Anime anime })
                await ToggleTimeMachineStatusSafelyAsync(anime.ID, AnimeTrackingStatus.Following);
        }

        private async Task ToggleTimeMachineStatusSafelyAsync(int animeId, AnimeTrackingStatus status)
        {
            try
            {
                await ViewModel.ToggleTimeMachineStatusAsync(animeId, status);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                ErrorInfoBar.Message = $"标记失败：{ex.Message}";
                ErrorInfoBar.IsOpen = true;
            }
        }

        private void OnTimeMachineYearClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string text }
                && int.TryParse(text, out var yearsAgo))
            {
                ViewModel.TimeMachineYearsAgo = yearsAgo;
            }

            // 点击已选中的按钮也保持选中。
            UpdateTimeMachineYearButtons();
        }

        private void UpdateTimeMachineYearButtons()
        {
            foreach (var button in TimeMachineYearButtons.Children.OfType<ToggleButton>())
            {
                button.IsChecked = button.Tag is string text
                    && int.TryParse(text, out var yearsAgo)
                    && yearsAgo == ViewModel.TimeMachineYearsAgo;
            }
        }

        /// <summary>打开番剧库并直接定位到时光机指向的那一季。</summary>
        private void OnTimeMachineOpenSeasonClick(object sender, RoutedEventArgs e)
            => _pluginNavigator.Navigate(typeof(PastSeasonPage), ViewModel.TimeMachineTarget);

        private async Task ToggleStatusSafelyAsync(int animeId, AnimeTrackingStatus status)
        {
            try
            {
                await ViewModel.ToggleStatusAsync(animeId, status);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                ErrorInfoBar.Message = $"标记失败：{ex.Message}";
                ErrorInfoBar.IsOpen = true;
            }
        }

        /// <summary>悬停时显示标记按钮并隐藏评分，避免两者重叠。</summary>
        private void OnPickPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement row)
                _hoveredPickRows.Add(row);
            SetPickActionsVisible(sender, true);
        }

        private void OnPickPointerExited(object sender, PointerRoutedEventArgs e)
        {
            var keepVisible = false;
            if (sender is FrameworkElement row)
            {
                _hoveredPickRows.Remove(row);
                keepVisible = ContainsKeyboardFocus(row);
            }
            SetPickActionsVisible(sender, keepVisible);
        }

        private void OnPickFocusChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement row)
                return;

            DispatcherQueue.TryEnqueue(() =>
                SetPickActionsVisible(
                    row,
                    _hoveredPickRows.Contains(row) || ContainsKeyboardFocus(row)));
        }

        /// <summary>只认键盘焦点，鼠标点过按钮留下的焦点不让浮层一直显示。</summary>
        private static bool ContainsKeyboardFocus(FrameworkElement element)
        {
            if (element.XamlRoot is null
                || FocusManager.GetFocusedElement(element.XamlRoot)
                    is not Control { FocusState: FocusState.Keyboard } focused)
                return false;

            DependencyObject? current = focused;
            while (current is not null)
            {
                if (ReferenceEquals(current, element))
                    return true;

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private static void SetPickActionsVisible(object sender, bool visible)
        {
            if (sender is not FrameworkElement row)
                return;

            if (row.FindName("PickActions") is UIElement actions)
                actions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (row.FindName("PickScore") is UIElement score)
                score.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>封面随行高变高，宽度按比例跟随。</summary>
        private void OnPickCoverHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement host && e.NewSize.Height > 0)
                host.Width = Math.Round(e.NewSize.Height * PickCoverAspect);
        }

        private void OnPickCoverLoaded(object sender, RoutedEventArgs e)
            => ConfigurePickCover(sender);

        private void OnPickCoverDataContextChanged(
            FrameworkElement sender,
            DataContextChangedEventArgs args)
        {
            if (sender.IsLoaded)
                ConfigurePickCover(sender);
        }

        private void OnPickCoverUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Image image)
                ManagedImageLoader.Cancel(image);
        }

        private static void ConfigurePickCover(object sender)
        {
            if (sender is not Image image)
                return;

            if (image.DataContext is Anime anime)
                ManagedImageLoader.ConfigureCover(image, anime.ID, anime.CoverURL, 40);
            else
                ManagedImageLoader.Cancel(image);
        }
    }
}
