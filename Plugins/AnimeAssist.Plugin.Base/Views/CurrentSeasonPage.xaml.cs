using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

using System.Collections.ObjectModel;
using System.Numerics;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class CurrentSeasonPage : Page
    {
        public CurrentSeasonViewModel ViewModel { get; }

        static bool _hasAutoScrolledOnce = false;
        private readonly List<Anime> _allAnime = new();
        private HashSet<int> _blockedIds = new();
        private DragDropService _dragDrop;
        private IDisposable? _dropHostRegistration;
        private CancellationTokenSource? _autoScrollCts;
        private Visual? _autoScrollEffectVisual;
        private TrackingService _tracking;
        private readonly IPluginNavigator _pluginNavigator;

        public CurrentSeasonPage(IAnimeDataSource dataSource, DragDropService dragDropService, TrackingService trackingService, IPluginNavigator pluginNavigator)
        {
            ViewModel = new CurrentSeasonViewModel(dataSource);
            _dragDrop = dragDropService;
            _tracking = trackingService;
            _pluginNavigator = pluginNavigator;
            InitializeComponent();

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
                            _allAnime.AddRange(AnimeListPresentation.Filter(
                                ViewModel.AnimeList,
                                _blockedIds));
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

            // 返回已缓存页面时重新读取屏蔽状态，移除刚屏蔽的条目。
            _ = LoadDragConfigAndBlockedAsync();

            if (_hasAutoScrolledOnce)
            {
                return;
            }

            // 首次启动时等待开屏淡出，再滚动到今日星期分组。
            _autoScrollCts?.Cancel();
            _autoScrollCts?.Dispose();
            _autoScrollCts = new CancellationTokenSource();
            _ = WaitForSplashAndAutoScrollAsync(_autoScrollCts.Token);
        }

        private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
        {
            _autoScrollCts?.Cancel();
            _autoScrollCts?.Dispose();
            _autoScrollCts = null;
            StopAutoScrollEffect();
            _dropHostRegistration?.Dispose();
            _dropHostRegistration = null;
        }

        private async Task WaitForSplashAndAutoScrollAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                if (_hasAutoScrolledOnce)
                {
                    return;
                }

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
                    await Task.WhenAny(
                        tcs.Task,
                        Task.Delay(10000, cancellationToken));
                    ViewModel.PropertyChanged -= handler;
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // 如果数据加载失败，不执行自动滚动
                if (ViewModel.IsError || !ViewModel.HasData)
                    return;

                // 另一个页面实例可能已在数据等待期间完成了定位。
                if (_hasAutoScrolledOnce)
                {
                    return;
                }

                // 等待开屏淡出完成（固定等待），开屏动画至少 2 秒显示 + 1.6 秒淡出
                await Task.Delay(3600, cancellationToken);

                // 执行自动跳转
                if (!_hasAutoScrolledOnce && ViewModel.WeekdayGroups.Count > 0)
                {
                    int todayIndex = DateTime.Now.DayOfWeek switch
                    {
                        DayOfWeek.Sunday => 6,
                        _ => (int)DateTime.Now.DayOfWeek - 1
                    };
                    if (await DelayedScrollToGroupAsync(
                            todayIndex,
                            cancellationToken))
                    {
                        _hasAutoScrolledOnce = true;
                    }
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
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


        private async Task<bool> DelayedScrollToGroupAsync(
            int index,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < 10; i++)
            {
                if (i > 0)
                    await Task.Delay(50, cancellationToken);

                var container = WeekdayRepeater.TryGetElement(index)
                    ?? WeekdayRepeater.GetOrCreateElement(index);
                WeekdayRepeater.UpdateLayout();
                if (container is not null
                    && RootScrollViewer.Content is UIElement scrollContent)
                {
                    var position = container
                        .TransformToVisual(scrollContent)
                        .TransformPoint(new Windows.Foundation.Point());
                    await ScrollToOffsetAsync(
                        Math.Max(0, position.Y),
                        cancellationToken);
                    PlayAutoScrollEffect(container);
                    return true;
                }
            }

            return false;
        }

        private async Task ScrollToOffsetAsync(
            double verticalOffset,
            CancellationToken cancellationToken)
        {
            var completed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ScrollViewerViewChangedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                if (!args.IsIntermediate)
                {
                    completed.TrySetResult();
                }
            };

            RootScrollViewer.ViewChanged += handler;
            try
            {
                var viewChanged = RootScrollViewer.ChangeView(
                    null,
                    verticalOffset,
                    null,
                    disableAnimation: false);
                if (!viewChanged)
                {
                    return;
                }

                await Task.WhenAny(
                    completed.Task,
                    Task.Delay(1200, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                RootScrollViewer.ViewChanged -= handler;
            }
        }

        private void PlayAutoScrollEffect(UIElement element)
        {
            StopAutoScrollEffect();

            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            visual.CenterPoint = new Vector3(
                (float)element.ActualSize.X / 2,
                (float)element.ActualSize.Y / 2,
                0);
            _autoScrollEffectVisual = visual;

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

        private void StopAutoScrollEffect()
        {
            if (_autoScrollEffectVisual is null)
            {
                return;
            }

            _autoScrollEffectVisual.StopAnimation("Scale.X");
            _autoScrollEffectVisual.StopAnimation("Scale.Y");
            _autoScrollEffectVisual.Scale = Vector3.One;
            _autoScrollEffectVisual = null;
        }

        private void OnAnimeCardClicked(object? sender, Views.Controls.AnimeCardClickedEventArgs e)
        {
            _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);
        }

        // ======== 拖放标记 ========

        private async Task LoadDragConfigAndBlockedAsync()
        {
            try
            {
                await _dragDrop.ReloadConfigAsync();
                _blockedIds = await _tracking.GetBlockedAnimeIdsAsync();
                // 无论数据是否已加载，都重新从原始数据过滤一次
                if (ViewModel.AnimeList.Count > 0)
                {
                    _allAnime.Clear();
                    _allAnime.AddRange(AnimeListPresentation.Filter(
                        ViewModel.AnimeList,
                        _blockedIds));
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

        // ======== 即时过滤 ========

        private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter(FilterBox.Text);
        }

        private void ApplyFilter(string query)
        {
            var filtered = AnimeListPresentation.Filter(
                _allAnime,
                titleQuery: query);
            ViewModel.WeekdayGroups.Clear();
            ViewModel.HasData = filtered.Count > 0;
            foreach (var group in
                AnimeListPresentation.GroupByWeekday(filtered))
            {
                ViewModel.WeekdayGroups.Add(group);
            }
        }
    }
}
