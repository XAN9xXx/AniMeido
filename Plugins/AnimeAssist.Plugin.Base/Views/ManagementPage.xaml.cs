using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Collections.ObjectModel;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class ManagementPage : Page
    {
        public ManagementViewModel ViewModel { get; }
        private readonly LocalSearchService _searchService;
        private readonly IPluginNavigator _pluginNavigator;
        private CancellationTokenSource? _searchCts;
        private readonly ObservableCollection<SearchResult> _searchResults = [];

        /// <summary>
        /// 用于 XAML 绑定的静态方法：根据 IsExpanded 返回 Visibility。
        /// </summary>
        public static Visibility TagAnimeListVisibility(bool isExpanded)
            => isExpanded ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// 用于 XAML 绑定的静态方法：获取番剧封面 URI（优先本地缓存，回退到远程或占位图）。
        /// </summary>
        public static Microsoft.UI.Xaml.Media.ImageSource GetCoverSource(int animeId, string? coverUrl)
            => new BitmapImage(ImageCacheHelper.GetImageUri(animeId, coverUrl)) { DecodePixelWidth = 128 };

        // 当前选中的导航卡片
        private Border? _currentTab;

        public ManagementPage(TrackingService trackingService, IAnimeDataSource dataSource, SavedTagService savedTagService, LocalSearchService searchService, IPluginNavigator pluginNavigator)
        {
            _searchService = searchService;
            _pluginNavigator = pluginNavigator;
            ViewModel = new ManagementViewModel(trackingService, dataSource, savedTagService);
            DataContext = ViewModel;
            InitializeComponent();

            ViewModel.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(ManagementViewModel.IsLoading):
                        LoadingOverlay.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                        LoadingRing.IsActive = ViewModel.IsLoading;
                        break;

                    case nameof(ManagementViewModel.IsError):
                        if (ViewModel.IsError)
                        {
                            ErrorInfoBar.Message = ViewModel.ErrorMessage;
                            ErrorInfoBar.IsOpen = true;
                        }
                        else
                        {
                            ErrorInfoBar.IsOpen = false;
                        }
                        break;

                    case nameof(ManagementViewModel.WatchingCount):
                        WatchingCountText.Text = $"追番中 ({ViewModel.WatchingCount})";
                        WatchingEmpty.Visibility = ViewModel.WatchingCount > 0 ? Visibility.Collapsed : Visibility.Visible;
                        break;

                    case nameof(ManagementViewModel.PlanToWatchCount):
                        PlanToWatchCountText.Text = $"补番中 ({ViewModel.PlanToWatchCount})";
                        PlanToWatchEmpty.Visibility = ViewModel.PlanToWatchCount > 0 ? Visibility.Collapsed : Visibility.Visible;
                        break;

                    case nameof(ManagementViewModel.NotInterestedCount):
                        NotInterestedCountText.Text = $"不感兴趣 ({ViewModel.NotInterestedCount})";
                        NotInterestedEmpty.Visibility = ViewModel.NotInterestedCount > 0 ? Visibility.Collapsed : Visibility.Visible;
                        break;

                    case nameof(ManagementViewModel.FollowingCount):
                        FollowingCountText.Text = $"关注 ({ViewModel.FollowingCount})";
                        FollowingEmpty.Visibility = ViewModel.FollowingCount > 0 ? Visibility.Collapsed : Visibility.Visible;
                        break;

                    case nameof(ManagementViewModel.CompletedCount):
                        CompletedCountText.Text = $"已看完 ({ViewModel.CompletedCount})";
                        CompletedEmpty.Visibility = ViewModel.CompletedCount > 0 ? Visibility.Collapsed : Visibility.Visible;
                        break;

                    case nameof(ManagementViewModel.DroppedCount):
                        DroppedCountText.Text = $"弃番 ({ViewModel.DroppedCount})";
                        DroppedEmpty.Visibility = ViewModel.DroppedCount > 0 ? Visibility.Collapsed : Visibility.Visible;
                        break;

                    case nameof(ManagementViewModel.BlockedCount):
                        BlockedCountText.Text = $"屏蔽 ({ViewModel.BlockedCount})";
                        BlockedEmpty.Visibility = ViewModel.BlockedCount > 0 ? Visibility.Collapsed : Visibility.Visible;
                        break;

                    case nameof(ManagementViewModel.HasTags):
                        TagEmptyText.Visibility = ViewModel.HasTags ? Visibility.Collapsed : Visibility.Visible;
                        break;
                }
            };

            // 主题切换时通过统一刷新方法恢复视觉状态
            this.ActualThemeChanged += (_, _) => RefreshNavigationVisualState();
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            // 初始选中"追番中"
            _currentTab = WatchingCard;
            RefreshNavigationVisualState();
            ViewModel.LoadDataCommand.Execute(null);
        }

        private void OnTabClicked(object sender, TappedRoutedEventArgs e)
        {
            // 点击 tab 时隐藏搜索结果
            SearchResultPanel.Visibility = Visibility.Collapsed;
            SearchBox.Text = "";

            WatchingPanel.Visibility = Visibility.Collapsed;
            PlanToWatchPanel.Visibility = Visibility.Collapsed;
            NotInterestedPanel.Visibility = Visibility.Collapsed;
            FollowingPanel.Visibility = Visibility.Collapsed;
            CompletedPanel.Visibility = Visibility.Collapsed;
            DroppedPanel.Visibility = Visibility.Collapsed;
            BlockedPanel.Visibility = Visibility.Collapsed;
            TagPanel.Visibility = Visibility.Collapsed;

            if (sender == (object)WatchingCard)
            {
                _currentTab = WatchingCard;
                WatchingPanel.Visibility = Visibility.Visible;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.Watching);
            }
            else if (sender == (object)PlanToWatchCard)
            {
                _currentTab = PlanToWatchCard;
                PlanToWatchPanel.Visibility = Visibility.Visible;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.PlanToWatch);
            }
            else if (sender == (object)NotInterestedCard)
            {
                _currentTab = NotInterestedCard;
                NotInterestedPanel.Visibility = Visibility.Visible;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.NotInterested);
            }
            else if (sender == (object)FollowingCard)
            {
                _currentTab = FollowingCard;
                FollowingPanel.Visibility = Visibility.Visible;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.Following);
            }
            else if (sender == (object)CompletedCard)
            {
                _currentTab = CompletedCard;
                CompletedPanel.Visibility = Visibility.Visible;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.Completed);
            }
            else if (sender == (object)DroppedCard)
            {
                _currentTab = DroppedCard;
                DroppedPanel.Visibility = Visibility.Visible;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.Dropped);
            }
            else if (sender == (object)BlockedCard)
            {
                _currentTab = BlockedCard;
                BlockedPanel.Visibility = Visibility.Visible;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.Blocked);
            }
            else if (sender == (object)TagCard)
            {
                _currentTab = TagCard;
                TagPanel.Visibility = Visibility.Visible;
                ViewModel.LoadTagsCommand.Execute(null);
            }

            RefreshNavigationVisualState();
        }

        /// <summary>
        /// 统一刷新导航视觉状态：先 ClearValue 清除 XAML/代码本地值，再通过 Style 切换。
        /// Style 内部使用 {ThemeResource}，因此主题切换后自动生效。
        /// 在 Loaded、OnTabClicked、ActualThemeChanged 后调用。
        /// </summary>
        private void RefreshNavigationVisualState()
        {
            var defaultCardStyle = (Style)Resources["ManagementNavCardDefaultStyle"];
            var selectedCardStyle = (Style)Resources["ManagementNavCardSelectedStyle"];
            var defaultLabelStyle = (Style)Resources["ManagementNavLabelDefaultStyle"];
            var selectedLabelStyle = (Style)Resources["ManagementNavLabelSelectedStyle"];
            var defaultIndicatorStyle = (Style)Resources["ManagementNavIndicatorDefaultStyle"];
            var selectedIndicatorStyle = (Style)Resources["ManagementNavIndicatorSelectedStyle"];
            var defaultIconStyle = (Style)Resources["ManagementNavIconDefaultStyle"];
            var selectedIconStyle = (Style)Resources["ManagementNavIconSelectedStyle"];

            // 清除本地值 + 设默认 Style
            void ResetNav(Border card, TextBlock label, Rectangle indicator, FontIcon icon)
            {
                card.ClearValue(Border.BackgroundProperty);
                card.Style = defaultCardStyle;

                label.ClearValue(TextBlock.ForegroundProperty);
                label.Style = defaultLabelStyle;

                indicator.ClearValue(Rectangle.FillProperty);
                indicator.ClearValue(Rectangle.VisibilityProperty);
                indicator.Style = defaultIndicatorStyle;

                icon.ClearValue(FontIcon.ForegroundProperty);
                icon.Style = defaultIconStyle;
            }

            ResetNav(WatchingCard, WatchingLabel, WatchingIndicator, WatchingIcon);
            ResetNav(PlanToWatchCard, PlanToWatchLabel, PlanToWatchIndicator, PlanToWatchIcon);
            ResetNav(NotInterestedCard, NotInterestedLabel, NotInterestedIndicator, NotInterestedIcon);
            ResetNav(FollowingCard, FollowingLabel, FollowingIndicator, FollowingIcon);
            ResetNav(CompletedCard, CompletedLabel, CompletedIndicator, CompletedIcon);
            ResetNav(DroppedCard, DroppedLabel, DroppedIndicator, DroppedIcon);
            ResetNav(BlockedCard, BlockedLabel, BlockedIndicator, BlockedIcon);
            ResetNav(TagCard, TagLabel, TagIndicator, TagIcon);

            // 设置选中态：先 ClearValue 再设 Style，确保 Style setter 生效
            if (_currentTab != null)
            {
                var label = GetLabelForCard(_currentTab);
                var indicator = GetIndicatorForCard(_currentTab);
                var icon = GetIconForCard(_currentTab);

                _currentTab.ClearValue(Border.BackgroundProperty);
                _currentTab.Style = selectedCardStyle;

                if (label != null)
                {
                    label.ClearValue(TextBlock.ForegroundProperty);
                    label.Style = selectedLabelStyle;
                }

                if (indicator != null)
                {
                    indicator.ClearValue(Rectangle.FillProperty);
                    indicator.ClearValue(Rectangle.VisibilityProperty);
                    indicator.Style = selectedIndicatorStyle;
                }

                if (icon != null)
                {
                    icon.ClearValue(FontIcon.ForegroundProperty);
                    icon.Style = selectedIconStyle;
                }
            }
        }

        private TextBlock? GetLabelForCard(Border card)
        {
            if (card == WatchingCard) return WatchingLabel;
            if (card == PlanToWatchCard) return PlanToWatchLabel;
            if (card == NotInterestedCard) return NotInterestedLabel;
            if (card == FollowingCard) return FollowingLabel;
            if (card == CompletedCard) return CompletedLabel;
            if (card == DroppedCard) return DroppedLabel;
            if (card == BlockedCard) return BlockedLabel;
            if (card == TagCard) return TagLabel;
            return null;
        }

        private Rectangle? GetIndicatorForCard(Border card)
        {
            if (card == WatchingCard) return WatchingIndicator;
            if (card == PlanToWatchCard) return PlanToWatchIndicator;
            if (card == NotInterestedCard) return NotInterestedIndicator;
            if (card == FollowingCard) return FollowingIndicator;
            if (card == CompletedCard) return CompletedIndicator;
            if (card == DroppedCard) return DroppedIndicator;
            if (card == BlockedCard) return BlockedIndicator;
            if (card == TagCard) return TagIndicator;
            return null;
        }

        private FontIcon? GetIconForCard(Border card)
        {
            if (card == WatchingCard) return WatchingIcon;
            if (card == PlanToWatchCard) return PlanToWatchIcon;
            if (card == NotInterestedCard) return NotInterestedIcon;
            if (card == FollowingCard) return FollowingIcon;
            if (card == CompletedCard) return CompletedIcon;
            if (card == DroppedCard) return DroppedIcon;
            if (card == BlockedCard) return BlockedIcon;
            if (card == TagCard) return TagIcon;
            return null;
        }

        private bool IsCardSelected(Border card)
            => card == _currentTab;

        private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border && !IsCardSelected(border))
            {
                // 先清除本地 Background，确保 Style 的默认背景不残留
                border.ClearValue(Border.BackgroundProperty);
                // 非选中项 hover 时应用主题背景
                var hoverBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AniMeidoNavItemHoverBackgroundBrush"];
                border.Background = hoverBrush;
            }
        }

        private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border)
            {
                // 通过统一刷新恢复正确的选中/非选中背景
                RefreshNavigationVisualState();
            }
        }

        private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
        {
        }

        private void OnCardPointerReleased(object sender, PointerRoutedEventArgs e)
        {
        }

        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Anime anime)
            {
                var tag = btn.Tag?.ToString();
                switch (tag)
                {
                    case "Watching":
                        ViewModel.RemoveFromWatchingCommand.Execute(anime.ID);
                        break;
                    case "PlanToWatch":
                        ViewModel.RemoveFromPlanCommand.Execute(anime.ID);
                        break;
                    case "NotInterested":
                        ViewModel.RemoveFromNotInterestedCommand.Execute(anime.ID);
                        break;
                    case "Following":
                        ViewModel.RemoveFromFollowingCommand.Execute(anime.ID);
                        break;
                    case "Completed":
                        ViewModel.RemoveFromCompletedCommand.Execute(anime.ID);
                        break;
                    case "Dropped":
                        ViewModel.RemoveFromDroppedCommand.Execute(anime.ID);
                        break;
                    case "Blocked":
                        ViewModel.RemoveFromBlockedCommand.Execute(anime.ID);
                        break;
                }
            }
        }

        private void OnItemTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Anime anime)
            {
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), anime.ID);
            }
        }

        private void OnCardEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(18, accent.R, accent.G, accent.B));
            }
        }

        private void OnCardExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
                border.Background = null;
        }

        private void OnCardPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                var visual = ElementCompositionPreview.GetElementVisual(border);
                var compositor = visual.Compositor;

                var scaleX = compositor.CreateScalarKeyFrameAnimation();
                scaleX.InsertKeyFrame(0.0f, 1.0f);
                scaleX.InsertKeyFrame(0.6f, 0.97f);
                scaleX.InsertKeyFrame(1.0f, 1.03f);
                scaleX.Duration = TimeSpan.FromMilliseconds(300);

                var scaleY = compositor.CreateScalarKeyFrameAnimation();
                scaleY.InsertKeyFrame(0.0f, 1.0f);
                scaleY.InsertKeyFrame(0.6f, 0.97f);
                scaleY.InsertKeyFrame(1.0f, 1.03f);
                scaleY.Duration = TimeSpan.FromMilliseconds(300);

                visual.CenterPoint = new System.Numerics.Vector3(
                    (float)border.ActualWidth / 2, (float)border.ActualHeight / 2, 0);
                visual.StartAnimation("Scale.X", scaleX);
                visual.StartAnimation("Scale.Y", scaleY);
                // 弹回
                var bounceX = compositor.CreateScalarKeyFrameAnimation();
                bounceX.InsertKeyFrame(0.0f, 1.03f);
                bounceX.InsertKeyFrame(1.0f, 1.0f);
                bounceX.Duration = TimeSpan.FromMilliseconds(200);
                var bounceY = compositor.CreateScalarKeyFrameAnimation();
                bounceY.InsertKeyFrame(0.0f, 1.03f);
                bounceY.InsertKeyFrame(1.0f, 1.0f);
                bounceY.Duration = TimeSpan.FromMilliseconds(200);
                visual.StartAnimation("Scale.X", bounceX);
                visual.StartAnimation("Scale.Y", bounceY);
            }
        }

        private void OnCardReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                var visual = ElementCompositionPreview.GetElementVisual(border);
                var compositor = visual.Compositor;
                var reset = compositor.CreateScalarKeyFrameAnimation();
                reset.InsertKeyFrame(1.0f, 1.0f);
                reset.Duration = TimeSpan.FromMilliseconds(100);
                visual.StartAnimation("Scale.X", reset);
                visual.StartAnimation("Scale.Y", reset);
            }
        }

        // ======== 搜索 ========

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            // 不自动搜索，由用户按回车触发
        }

        private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                _ = PerformSearchAsync();
            }
        }

        private async Task PerformSearchAsync()
        {
            if (_searchService == null) return;
            var query = SearchBox.Text;
            if (string.IsNullOrWhiteSpace(query))
            {
                HideSearchResults();
                return;
            }

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            ShowSearchPanel();
            SearchResultCount.Text = "搜索中…";
            _searchResults.Clear();

            try
            {
                var results = await _searchService.SearchTrackedAsync(query, ct);

                SearchResultCount.Text = $"搜索结果：共 {results.Count} 条";

                // 一次性替换数据源（ListView 虚拟化，仅渲染可见项）
                _searchResults.Clear();
                foreach (var r in results)
                    _searchResults.Add(r);
            }
            catch (OperationCanceledException) { }
#pragma warning disable CA1031 // 搜索异常不应崩溃页面，错误信息已通过 SearchResultCount 展示
            catch (Exception ex)
            {
                SearchResultCount.Text = $"搜索出错：{ex.Message}";
                _searchResults.Clear();
            }
#pragma warning restore CA1031
        }

        private void ShowSearchPanel()
        {
            SearchResultPanel.Visibility = Visibility.Visible;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            WatchingPanel.Visibility = Visibility.Collapsed;
            PlanToWatchPanel.Visibility = Visibility.Collapsed;
            NotInterestedPanel.Visibility = Visibility.Collapsed;
            FollowingPanel.Visibility = Visibility.Collapsed;
            CompletedPanel.Visibility = Visibility.Collapsed;
            DroppedPanel.Visibility = Visibility.Collapsed;
            BlockedPanel.Visibility = Visibility.Collapsed;
            TagPanel.Visibility = Visibility.Collapsed;
        }

        private void HideSearchResults()
        {
            SearchResultPanel.Visibility = Visibility.Collapsed;
            _searchResults.Clear();

            // 恢复当前选中的 tab
            var selectedTab = GetSelectedTab();
            if (selectedTab != null)
                selectedTab.Visibility = Visibility.Visible;
        }

        private void OnSearchResultClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SearchResult result)
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), result.Anime.ID);
        }

        private ScrollViewer? GetSelectedTab()
        {
            if (_currentTab == WatchingCard) return WatchingPanel;
            if (_currentTab == PlanToWatchCard) return PlanToWatchPanel;
            if (_currentTab == NotInterestedCard) return NotInterestedPanel;
            if (_currentTab == FollowingCard) return FollowingPanel;
            if (_currentTab == CompletedCard) return CompletedPanel;
            if (_currentTab == DroppedCard) return DroppedPanel;
            if (_currentTab == BlockedCard) return BlockedPanel;
            if (_currentTab == TagCard) return TagPanel;
            return WatchingPanel;
        }

        // ======== Tag 管理 ========

        private void OnTagItemTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TagItem tag)
            {
                ViewModel.ToggleTagCommand.Execute(tag);
            }
        }

        private void OnTagAnimeTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Anime anime)
            {
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), anime.ID);
            }
        }
    }
}


