using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class ManagementPage : Page
    {
        public ManagementViewModel ViewModel { get; }
        private LocalSearchService? _searchService;
        private CancellationTokenSource? _searchCts;

        /// <summary>
        /// 用于 XAML 绑定的静态方法：根据 IsExpanded 返回 Visibility。
        /// </summary>
        public static Visibility TagAnimeListVisibility(bool isExpanded)
            => isExpanded ? Visibility.Visible : Visibility.Collapsed;

        public ManagementPage()
        {
            var ts = AppServices.Provider!.GetRequiredService<TrackingService>();
            var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
            var sts = AppServices.Provider!.GetRequiredService<SavedTagService>();
            _searchService = AppServices.Provider!.GetRequiredService<LocalSearchService>();
            ViewModel = new ManagementViewModel(ts, ds, sts);
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
                            ErrorInfoBar.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            ErrorInfoBar.IsOpen = false;
                            ErrorInfoBar.Visibility = Visibility.Collapsed;
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

            if (sender == WatchingCard)
            {
                WatchingPanel.Visibility = Visibility.Visible;
                WatchingIndicator.Visibility = Visibility.Visible;
                WatchingIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                WatchingLabel.Foreground = selectedBrush;
                WatchingCard.Background = selectedBg;
            }
            else if (sender == PlanToWatchCard)
            {
                PlanToWatchPanel.Visibility = Visibility.Visible;
                PlanToWatchIndicator.Visibility = Visibility.Visible;
                PlanToWatchIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                PlanToWatchLabel.Foreground = selectedBrush;
                PlanToWatchCard.Background = selectedBg;
            }
            else if (sender == NotInterestedCard)
            {
                NotInterestedPanel.Visibility = Visibility.Visible;
                NotInterestedIndicator.Visibility = Visibility.Visible;
                NotInterestedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                NotInterestedLabel.Foreground = selectedBrush;
                NotInterestedCard.Background = selectedBg;
            }
            else if (sender == FollowingCard)
            {
                FollowingPanel.Visibility = Visibility.Visible;
                FollowingIndicator.Visibility = Visibility.Visible;
                FollowingIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                FollowingLabel.Foreground = selectedBrush;
                FollowingCard.Background = selectedBg;
            }
            else if (sender == CompletedCard)
            {
                CompletedPanel.Visibility = Visibility.Visible;
                CompletedIndicator.Visibility = Visibility.Visible;
                CompletedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                CompletedLabel.Foreground = selectedBrush;
                CompletedCard.Background = selectedBg;
            }
            else if (sender == DroppedCard)
            {
                DroppedPanel.Visibility = Visibility.Visible;
                DroppedIndicator.Visibility = Visibility.Visible;
                DroppedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                DroppedLabel.Foreground = selectedBrush;
                DroppedCard.Background = selectedBg;
            }
            else if (sender == BlockedCard)
            {
                BlockedPanel.Visibility = Visibility.Visible;
                BlockedIndicator.Visibility = Visibility.Visible;
                BlockedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                BlockedLabel.Foreground = selectedBrush;
                BlockedCard.Background = selectedBg;
            }
            else if (sender == TagCard)
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
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
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

            // 切换到搜索面板
            ShowSearchPanel();

            var loadingText = new TextBlock
            {
                Text = "搜索中…",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(160, 128, 128, 128)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 0),
            };
            SearchResultContainer.Children.Clear();
            SearchResultContainer.Children.Add(loadingText);

            try
            {
                var results = await _searchService.SearchTrackedAsync(query, ct);

                SearchResultContainer.Children.Clear();

                SearchResultCount.Text = $"搜索结果：共 {results.Count} 条";

                if (results.Count == 0)
                {
                    var emptyText = new TextBlock
                    {
                        Text = $"未找到与「{query}」匹配的番剧",
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(160, 128, 128, 128)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 24, 0, 0),
                    };
                    SearchResultContainer.Children.Add(emptyText);
                    return;
                }

                foreach (var result in results)
                {
                    var card = CreateSearchResultCard(result);
                    SearchResultContainer.Children.Add(card);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SearchResultContainer.Children.Clear();
                var errText = new TextBlock
                {
                    Text = $"搜索出错：{ex.Message}",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 80, 80)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 24, 0, 0),
                };
                SearchResultContainer.Children.Add(errText);
            }
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
            SearchResultContainer.Children.Clear();

            // 恢复当前选中的 tab
            var selectedTab = GetSelectedTab();
            if (selectedTab != null)
                selectedTab.Visibility = Visibility.Visible;
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

        private Border CreateSearchResultCard(SearchResult result)
        {
            var anime = result.Anime;
            var statusLabel = GetStatusLabel(result.TrackingStatus);
            var statusColor = GetStatusColor(result.TrackingStatus);

            var coverBorder = new Border
            {
                Width = 64,
                Height = 90,
                CornerRadius = new CornerRadius(4),
                Child = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Source = new BitmapImage(ImageCacheHelper.GetImageUri(anime.ID, anime.CoverURL)),
                }
            };

            var titleText = new TextBlock
            {
                Text = anime.Title,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var statusBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = statusColor,
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = statusLabel,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                }
            };

            var descText = new TextBlock
            {
                Text = anime.Description,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 128, 128, 128)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
                TextWrapping = TextWrapping.WrapWholeWords,
            };

            var infoStack = new StackPanel
            {
                Spacing = 4,
                Children = { titleText, statusBadge, descText }
            };

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Tag = anime.ID,
            };
            var innerGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(64) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                },
                ColumnSpacing = 12,
            };
            innerGrid.Children.Add(coverBorder);
            Grid.SetColumn(infoStack, 1);
            innerGrid.Children.Add(infoStack);
            card.Child = innerGrid;

            card.Tapped += (s, e) =>
            {
                if (s is Border b && b.Tag is int id)
                    Frame.Navigate(typeof(AnimeDetailPage), id);
            };

            // 后台缓存图片
            if (!ImageCacheHelper.HasLocalCache(anime.ID) && anime.CoverURL != null)
                _ = ImageCacheHelper.CacheImageAsync(anime.ID, anime.CoverURL);

            return card;
        }

        private static string GetStatusLabel(AnimeTrackingStatus status) => status switch
        {
            AnimeTrackingStatus.Watching => "追番",
            AnimeTrackingStatus.PlanToWatch => "补番",
            AnimeTrackingStatus.NotInterested => "不感兴趣",
            AnimeTrackingStatus.Following => "关注",
            AnimeTrackingStatus.Completed => "已看完",
            AnimeTrackingStatus.Dropped => "已弃番",
            AnimeTrackingStatus.Blocked => "屏蔽",
            _ => "未标记",
        };

        private static SolidColorBrush GetStatusColor(AnimeTrackingStatus status) => status switch
        {
            AnimeTrackingStatus.Watching => new SolidColorBrush(Color.FromArgb(220, 0x44, 0x88, 0xFF)),
            AnimeTrackingStatus.PlanToWatch => new SolidColorBrush(Color.FromArgb(220, 0x44, 0xFF, 0x88)),
            AnimeTrackingStatus.NotInterested => new SolidColorBrush(Color.FromArgb(220, 0xFF, 0x44, 0x44)),
            AnimeTrackingStatus.Following => new SolidColorBrush(Color.FromArgb(220, 0xFF, 0xAA, 0x00)),
            AnimeTrackingStatus.Completed => new SolidColorBrush(Color.FromArgb(220, 0x88, 0x44, 0xFF)),
            AnimeTrackingStatus.Dropped => new SolidColorBrush(Color.FromArgb(220, 0x88, 0x88, 0x88)),
            AnimeTrackingStatus.Blocked => new SolidColorBrush(Color.FromArgb(220, 0x44, 0x44, 0x44)),
            _ => new SolidColorBrush(Color.FromArgb(160, 0x88, 0x88, 0x88)),
        };

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
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
            }
        }
    }
}
