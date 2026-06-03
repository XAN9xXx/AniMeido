using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class CurrentSeasonPage : Page
    {
        public CurrentSeasonViewModel ViewModel { get; }

        static bool _hasAutoScrolledOnce = false;
        private bool _loadedHandlerAttached;
        private readonly List<Anime> _allAnime = new();
        private HashSet<int> _blockedIds = new();
        private DragDropService _dragDrop;
        private TrackingService _tracking;
        private readonly IPluginNavigator _pluginNavigator;

        public CurrentSeasonPage(IAnimeDataSource dataSource, DragDropService dragDropService, TrackingService trackingService, IPluginNavigator pluginNavigator)
        {
            ViewModel = new CurrentSeasonViewModel(dataSource);
            _dragDrop = dragDropService;
            _tracking = trackingService;
            _pluginNavigator = pluginNavigator;
            InitializeComponent();

            _ = LoadDragConfigAndBlockedAsync();

            ViewModel.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(CurrentSeasonViewModel.IsLoading):
                        UpdateOverlayState();
                        if (!ViewModel.IsLoading)
                        {
                            UpdateViewState();
                            // 保存原始数据用于过滤（_blockedIds 此时应已加载）
                            _allAnime.Clear();
                            _allAnime.AddRange(ViewModel.AnimeList.Where(a => !_blockedIds.Contains(a.ID)));
                            // 重建分组以反映过滤后的数据
                            ApplyFilter(FilterBox.Text);
                        }

                        // 首次打开时自动跳转到今天对应的星期分组
                        // （开屏模式下由 MainWindow 在淡出后触发）
                        break;

                    case nameof(CurrentSeasonViewModel.ErrorMessage):
                    case nameof(CurrentSeasonViewModel.IsError):
                        UpdateOverlayState();
                        UpdateViewState();
                        break;

                    case nameof(CurrentSeasonViewModel.HasData):
                        UpdateViewState();
                        break;
                }
            };

            ViewModel.LoadSeasonalAnimeCommand.Execute(null);
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (_loadedHandlerAttached) return;
            _loadedHandlerAttached = true;

            var rootGrid = (Grid)sender;
            rootGrid.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnCapturedPointerPressed), true);
            rootGrid.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(OnRootPointerReleased), true);
            rootGrid.AddHandler(UIElement.PointerCanceledEvent,
                new PointerEventHandler(OnRootPointerCanceled), true);

            // 等待开屏淡出完成后，自动跳转到今日星期分组
            // 开屏淡出在 FirstPageLoaded 信号 + 最低 2 秒显示 + 淡出动画后完成
            _ = WaitForSplashAndAutoScrollAsync();
        }

        private async Task WaitForSplashAndAutoScrollAsync()
        {
            try
            {
                // 等待数据加载完成或失败（含超时兜底）
                if (!ViewModel.HasData && !ViewModel.IsError)
                {
                    var tcs = new TaskCompletionSource();
                    System.ComponentModel.PropertyChangedEventHandler? handler = null;
                    handler = (_, e) =>
                    {
                        if (e.PropertyName is nameof(CurrentSeasonViewModel.HasData) or nameof(CurrentSeasonViewModel.IsError))
                        {
                            ViewModel.PropertyChanged -= handler;
                            tcs.TrySetResult();
                        }
                    };
                    ViewModel.PropertyChanged += handler;

                    // 最多等 10 秒，防止错误状态下永久等待
                    await Task.WhenAny(tcs.Task, Task.Delay(10000));
                    ViewModel.PropertyChanged -= handler;
                }

                // 如果数据加载失败，不执行自动滚动
                if (ViewModel.IsError || !ViewModel.HasData)
                    return;

                // 等待开屏淡出完成（固定等待），开屏动画至少 2 秒显示 + 1.6 秒淡出
                await Task.Delay(3600);

                // 执行自动跳转
                if (!_hasAutoScrolledOnce && ViewModel.WeekdayGroups.Count > 0)
                {
                    _hasAutoScrolledOnce = true;
                    int todayIndex = DateTime.Now.DayOfWeek switch
                    {
                        DayOfWeek.Sunday => 6,
                        _ => (int)DateTime.Now.DayOfWeek - 1
                    };
                    _ = DispatcherQueue.TryEnqueue(() => DelayedScrollToGroup(todayIndex));
                }
            }
#pragma warning disable CA1031 // 开屏动画失败不应影响页面
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CurrentSeasonPage] WaitForSplashAndAutoScrollAsync failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        private void UpdateViewState()
        {
            if (ViewModel.IsError)
            {
                ErrorInfoBar.Message = ViewModel.ErrorMessage;
                ErrorInfoBar.IsOpen = true;
                EmptyState.Visibility = Visibility.Collapsed;
            }
            else
            {
                ErrorInfoBar.IsOpen = false;
                EmptyState.Visibility = !ViewModel.IsLoading && !ViewModel.HasData
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void UpdateOverlayState()
        {
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


        private async void DelayedScrollToGroup(int index)
        {
            for (int i = 0; i < 10; i++)
            {
                if (i > 0)
                    await Task.Delay(50);

                WeekdayRepeater.UpdateLayout();

                var container = WeekdayRepeater.ContainerFromIndex(index) as UIElement;
                if (container is not null)
                {
                    container.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = true,
                        VerticalOffset = 0
                    });
                    await Task.Delay(500);
                    PlayBringIntoViewEffect(container);
                    return;
                }
            }
        }

        private void OnWeekdayItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), anime.ID);
        }

        private static void PlayBringIntoViewEffect(UIElement element)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            visual.CenterPoint = new System.Numerics.Vector3(
                (float)element.ActualSize.X / 2,
                (float)element.ActualSize.Y / 2,
                0);

            var scaleX = compositor.CreateScalarKeyFrameAnimation();
            scaleX.InsertKeyFrame(0.0f, 1.0f);
            scaleX.InsertKeyFrame(0.6f, 0.97f);
            scaleX.InsertKeyFrame(0.8f, 1.01f);
            scaleX.InsertKeyFrame(1.0f, 1.0f);
            scaleX.Duration = TimeSpan.FromMilliseconds(800);

            var scaleY = compositor.CreateScalarKeyFrameAnimation();
            scaleY.InsertKeyFrame(0.0f, 1.0f);
            scaleY.InsertKeyFrame(0.6f, 0.97f);
            scaleY.InsertKeyFrame(0.8f, 1.01f);
            scaleY.InsertKeyFrame(1.0f, 1.0f);
            scaleY.Duration = TimeSpan.FromMilliseconds(800);

            visual.StartAnimation("Scale.X", scaleX);
            visual.StartAnimation("Scale.Y", scaleY);
        }

        // ======== 拖放标记 ========

        private async Task LoadDragConfigAndBlockedAsync()
        {
            try
            {
                await _dragDrop.ReloadConfigAsync();
                var blocked = await _tracking.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Blocked);
                _blockedIds = blocked.ToHashSet();
                // 无论数据是否已加载，都重新从原始数据过滤一次
                if (ViewModel.AnimeList.Count > 0)
                {
                    _allAnime.Clear();
                    _allAnime.AddRange(ViewModel.AnimeList.Where(a => !_blockedIds.Contains(a.ID)));
                    ApplyFilter(FilterBox.Text);
                }
            }
#pragma warning disable CA1031 // 拖放/屏蔽配置加载失败不阻塞页面
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CurrentSeasonPage] LoadDragConfigAndBlockedAsync failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        // ======== 自定义拖放 ========

        private void OnCapturedPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _dragDrop.HandlePointerPressed(this, e);
        }

        private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            _dragDrop.HandlePointerMoved(this, DragOverlay, e, DragAction.PlanToWatch);
        }

        private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _dragDrop.HandlePointerReleased(DragOverlay, e);
            CleanupOverlayAfterDrag();
        }

        private void OnRootPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _dragDrop.HandlePointerCanceled(DragOverlay);
            CleanupOverlayAfterDrag();
        }

        private void CleanupOverlayAfterDrag()
        {
            if (!_dragDrop.IsDragging)
            {
                // 移除 Ghost 和 Zones
                if (_dragDrop.DragGhost != null)
                    DragOverlay.Children.Remove(_dragDrop.DragGhost);
                foreach (var zone in _dragDrop.ActiveZones)
                    DragOverlay.Children.Remove(zone.Border);
                DragOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // ======== 即时过滤 ========

        private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter(FilterBox.Text);
        }

        private void ApplyFilter(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                // 恢复全部分组
                ViewModel.HasData = _allAnime.Count > 0;
                ViewModel.WeekdayGroups.Clear();
                var groups = _allAnime
                    .GroupBy(a => a.Weekday)
                    .OrderBy(g => g.Key ?? 99)
                    .Select(g => new WeekdayGroup
                    {
                        WeekdayName = g.Key switch
                        {
                            1 => "周一",
                            2 => "周二",
                            3 => "周三",
                            4 => "周四",
                            5 => "周五",
                            6 => "周六",
                            7 => "周日",
                            _ => "其他",
                        },
                        Items = new ObservableCollection<Anime>(g)
                    });
                foreach (var group in groups)
                    ViewModel.WeekdayGroups.Add(group);
                return;
            }

            var lower = query.ToLowerInvariant();
            var filtered = _allAnime
                .Where(a => a.Title.Contains(lower, StringComparison.OrdinalIgnoreCase))
                .ToList();

            ViewModel.WeekdayGroups.Clear();
            if (filtered.Count == 0)
            {
                // 无匹配时显示提示（通过空状态处理）
                ViewModel.HasData = false;
                return;
            }

            ViewModel.HasData = true;
            var filteredGroups = filtered
                .GroupBy(a => a.Weekday)
                .OrderBy(g => g.Key ?? 99)
                .Select(g => new WeekdayGroup
                {
                    WeekdayName = g.Key switch
                    {
                        1 => "周一",
                        2 => "周二",
                        3 => "周三",
                        4 => "周四",
                        5 => "周五",
                        6 => "周六",
                        7 => "周日",
                        _ => "其他",
                    },
                    Items = new ObservableCollection<Anime>(g)
                });
            foreach (var group in filteredGroups)
                ViewModel.WeekdayGroups.Add(group);
        }
    }
}