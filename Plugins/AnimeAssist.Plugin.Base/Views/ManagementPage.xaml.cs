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

            var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            WatchingCard.Background = transparent;
            PlanToWatchCard.Background = transparent;
            NotInterestedCard.Background = transparent;
            FollowingCard.Background = transparent;
            CompletedCard.Background = transparent;
            DroppedCard.Background = transparent;
            BlockedCard.Background = transparent;
            TagCard.Background = transparent;

            // 重置所有指示器和文字颜色
            WatchingIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            WatchingIndicator.Visibility = Visibility.Collapsed;
            PlanToWatchIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            PlanToWatchIndicator.Visibility = Visibility.Collapsed;
            NotInterestedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            NotInterestedIndicator.Visibility = Visibility.Collapsed;
            FollowingIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            FollowingIndicator.Visibility = Visibility.Collapsed;
            CompletedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            CompletedIndicator.Visibility = Visibility.Collapsed;
            DroppedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            DroppedIndicator.Visibility = Visibility.Collapsed;
            BlockedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            BlockedIndicator.Visibility = Visibility.Collapsed;
            TagIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            TagIndicator.Visibility = Visibility.Collapsed;

            var defaultBrush = (Microsoft.UI.Xaml.Media.Brush)Resources["TabTextDefaultBrush"];
            var selectedBrush = (Microsoft.UI.Xaml.Media.Brush)Resources["TabTextSelectedBrush"];
            WatchingLabel.Foreground = defaultBrush;
            PlanToWatchLabel.Foreground = defaultBrush;
            NotInterestedLabel.Foreground = defaultBrush;
            FollowingLabel.Foreground = defaultBrush;
            CompletedLabel.Foreground = defaultBrush;
            DroppedLabel.Foreground = defaultBrush;
            BlockedLabel.Foreground = defaultBrush;
            TagLabel.Foreground = defaultBrush;

            // 设置选中项
            var accentColor = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            var selectedBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(20, accentColor.R, accentColor.G, accentColor.B));

            if (sender == (object)WatchingCard)
            {
                WatchingPanel.Visibility = Visibility.Visible;
                WatchingIndicator.Visibility = Visibility.Visible;
                WatchingIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                WatchingLabel.Foreground = selectedBrush;
                WatchingCard.Background = selectedBg;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.Watching);
            }
            else if (sender == (object)PlanToWatchCard)
            {
                PlanToWatchPanel.Visibility = Visibility.Visible;
                PlanToWatchIndicator.Visibility = Visibility.Visible;
                PlanToWatchIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                PlanToWatchLabel.Foreground = selectedBrush;
                PlanToWatchCard.Background = selectedBg;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.PlanToWatch);
            }
            else if (sender == (object)NotInterestedCard)
            {
                NotInterestedPanel.Visibility = Visibility.Visible;
                NotInterestedIndicator.Visibility = Visibility.Visible;
                NotInterestedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                NotInterestedLabel.Foreground = selectedBrush;
                NotInterestedCard.Background = selectedBg;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.NotInterested);
            }
            else if (sender == (object)FollowingCard)
            {
                FollowingPanel.Visibility = Visibility.Visible;
                FollowingIndicator.Visibility = Visibility.Visible;
                FollowingIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                FollowingLabel.Foreground = selectedBrush;
                FollowingCard.Background = selectedBg;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.Following);
            }
            else if (sender == (object)CompletedCard)
            {
                CompletedPanel.Visibility = Visibility.Visible;
                CompletedIndicator.Visibility = Visibility.Visible;
                CompletedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                CompletedLabel.Foreground = selectedBrush;
                CompletedCard.Background = selectedBg;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.Completed);
            }
            else if (sender == (object)DroppedCard)
            {
                DroppedPanel.Visibility = Visibility.Visible;
                DroppedIndicator.Visibility = Visibility.Visible;
                DroppedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                DroppedLabel.Foreground = selectedBrush;
                DroppedCard.Background = selectedBg;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.Dropped);
            }
            else if (sender == (object)BlockedCard)
            {
                BlockedPanel.Visibility = Visibility.Visible;
                BlockedIndicator.Visibility = Visibility.Visible;
                BlockedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                BlockedLabel.Foreground = selectedBrush;
                BlockedCard.Background = selectedBg;
                _ = ViewModel.LoadPanelAnimeAsync(AnimeTrackingStatus.Blocked);
            }
            else if (sender == (object)TagCard)
            {
                TagPanel.Visibility = Visibility.Visible;
                TagIndicator.Visibility = Visibility.Visible;
                TagIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                TagLabel.Foreground = selectedBrush;
                TagCard.Background = selectedBg;
                ViewModel.LoadTagsCommand.Execute(null);
            }
        }

        private bool IsCardSelected(Border card)
        {
            return card == WatchingCard && WatchingPanel.Visibility == Visibility.Visible
                || card == PlanToWatchCard && PlanToWatchPanel.Visibility == Visibility.Visible
                || card == NotInterestedCard && NotInterestedPanel.Visibility == Visibility.Visible
                || card == FollowingCard && FollowingPanel.Visibility == Visibility.Visible
                || card == CompletedCard && CompletedPanel.Visibility == Visibility.Visible
                || card == DroppedCard && DroppedPanel.Visibility == Visibility.Visible
                || card == BlockedCard && BlockedPanel.Visibility == Visibility.Visible
                || card == TagCard && TagPanel.Visibility == Visibility.Visible;
        }

        private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            var border = sender as Border;
            if (border != null)
            {
                var color = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                byte alpha = IsCardSelected(border) ? (byte)40 : (byte)25;
                border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B));
            }
        }

        private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
        {
            var border = sender as Border;
            if (border != null)
            {
                if (!IsCardSelected(border))
                    border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                else
                {
                    var color = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                    border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(20, color.R, color.G, color.B));
                }
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
            if (WatchingIndicator.Visibility == Visibility.Visible) return WatchingPanel;
            if (PlanToWatchIndicator.Visibility == Visibility.Visible) return PlanToWatchPanel;
            if (NotInterestedIndicator.Visibility == Visibility.Visible) return NotInterestedPanel;
            if (FollowingIndicator.Visibility == Visibility.Visible) return FollowingPanel;
            if (CompletedIndicator.Visibility == Visibility.Visible) return CompletedPanel;
            if (DroppedIndicator.Visibility == Visibility.Visible) return DroppedPanel;
            if (BlockedIndicator.Visibility == Visibility.Visible) return BlockedPanel;
            if (TagIndicator.Visibility == Visibility.Visible) return TagPanel;
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
