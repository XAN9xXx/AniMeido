using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class CurrentSeasonPage : Page
    {
        // 滚动位置可能带小数，判断是否到头时留一点容差，避免停在边界附近仍显示渐隐。
        private const double EdgeTolerance = 2;
        // 本季发现每行的尺寸，与 XAML 中的 UniformGridLayout 保持一致。
        private const double PickHeight = 64;
        private const double PickRowSpacing = 8;
        private const double PickMinWidth = 260;
        private const double PickColumnSpacing = 10;
        // 发现面板除列表外占用的高度：上下内边距、边框、标题行与行间距。
        private const double DiscoverChromeHeight = 14 + 14 + 2 + 24 + 10;
        // 发现面板左右内边距与边框。
        private const double DiscoverChromeWidth = 16 + 16 + 2;

        public CurrentSeasonViewModel ViewModel { get; }

        private readonly DragDropService _dragDrop;
        private readonly IPluginNavigator _pluginNavigator;
        private IDisposable? _dropHostRegistration;
        private double _wheelTarget = double.NaN;

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

                    case nameof(CurrentSeasonViewModel.DiscoverPicks):
                        UpdateDiscoverVisibility();
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

            ApplyFilterLayout();
        }

        private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.LoadSeasonalAnimeCommand.Cancel();
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
        /// 按天：番剧区高度贴合一行卡片，剩余高度给本季发现。
        /// 搜索或只看“我的”：番剧区占满剩余高度显示结果网格，本季发现隐藏。
        /// </summary>
        private void ApplyFilterLayout()
        {
            var filtering = ViewModel.IsFiltering;
            RootGrid.RowDefinitions[2].Height = filtering
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
            RootGrid.RowDefinitions[3].Height = filtering
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
            DiscoverHost.Visibility = filtering ? Visibility.Collapsed : Visibility.Visible;
            ShelfHost.Visibility = filtering ? Visibility.Collapsed : Visibility.Visible;
            ResultsScroller.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;
            UpdateScopeButtons();
        }

        private void OnDiscoverHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var rows = (int)Math.Floor(
                (e.NewSize.Height - DiscoverChromeHeight + PickRowSpacing)
                / (PickHeight + PickRowSpacing));
            var columns = Math.Clamp(
                (int)Math.Floor(
                    (e.NewSize.Width - DiscoverChromeWidth + PickColumnSpacing)
                    / (PickMinWidth + PickColumnSpacing)),
                1,
                3);
            ViewModel.SetDiscoverCapacity(Math.Max(0, rows) * columns);
            UpdateDiscoverVisibility();
        }

        private void UpdateDiscoverVisibility()
            => DiscoverPanel.Visibility = ViewModel.HasDiscoverPicks
                ? Visibility.Visible
                : Visibility.Collapsed;

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

        private async void OnPickWatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Anime anime })
            {
                await ToggleStatusSafelyAsync(anime.ID, AnimeTrackingStatus.Watching);
            }
        }

        private async void OnPickFollowClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Anime anime })
            {
                await ToggleStatusSafelyAsync(anime.ID, AnimeTrackingStatus.Following);
            }
        }

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
            => SetPickActionsVisible(sender, true);

        private void OnPickPointerExited(object sender, PointerRoutedEventArgs e)
            => SetPickActionsVisible(sender, false);

        private static void SetPickActionsVisible(object sender, bool visible)
        {
            if (sender is not FrameworkElement row)
                return;

            if (row.FindName("PickActions") is UIElement actions)
                actions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (row.FindName("PickScore") is UIElement score)
                score.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
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
