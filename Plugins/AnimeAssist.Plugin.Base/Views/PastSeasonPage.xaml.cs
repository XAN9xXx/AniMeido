using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class PastSeasonPage : Page
    {
        private const int EarliestSupportedYear = 1900;
        private readonly PastSeasonViewModel _viewModel;
        public PastSeasonViewModel ViewModel => _viewModel;
        private readonly List<Anime> _allAnime = new();
        private HashSet<int> _blockedIds = new();
        private DragDropService _dragDrop;
        private TrackingService _tracking;
        private IPluginNavigator _pluginNavigator;
        private CancellationTokenSource? _loadCts;
        private int _loadVersion;
        private bool _isRebuilding; // 防止 ComboBox 重建期间事件穿透

        public PastSeasonPage(IAnimeDataSource dataSource, DragDropService dragDropService, TrackingService trackingService, IPluginNavigator pluginNavigator)
        {
            _viewModel = new PastSeasonViewModel(dataSource);
            _dragDrop = dragDropService;
            _tracking = trackingService;
            _pluginNavigator = pluginNavigator;
            InitializeComponent();

            ViewModel.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(PastSeasonViewModel.IsLoading):
                        // 覆盖层显隐由 LoadSeasonAsync 直接控制，不依赖 PropertyChanged 回调
                        if (!ViewModel.IsLoading)
                            UpdateViewState();
                        break;

                    case nameof(PastSeasonViewModel.ErrorMessage):
                    case nameof(PastSeasonViewModel.IsError):
                        UpdateOverlayState();
                        UpdateViewState();
                        break;

                    case nameof(PastSeasonViewModel.HasData):
                        UpdateViewState();
                        break;

                    case nameof(PastSeasonViewModel.TotalCount):
                        if (ViewModel.TotalCount > 0)
                        {
                            StatsCard.Visibility = Visibility.Visible;
                            TotalCountText.Text = ViewModel.TotalCount.ToString();
                            // 数据加载完成后保存原始列表、显示过滤框
                            _allAnime.Clear();
                            _allAnime.AddRange(AnimeListPresentation.Filter(
                                ViewModel.AnimeList,
                                _blockedIds));
                            FilterCard.Visibility = Visibility.Visible;
                            FilterBox.Text = "";
                        }
                        else
                        {
                            StatsCard.Visibility = Visibility.Collapsed;
                        }
                        break;
                }
            };

            InitializeComboBoxes();
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

        private void InitializeComboBoxes()
        {
            var latestCompleted = GetLatestCompletedSeason(DateTime.Now);
            for (int y = EarliestSupportedYear;
                y <= latestCompleted.Year;
                y++)
            {
                YearComboBox.Items.Add(y);
            }

            YearComboBox.SelectedItem = latestCompleted.Year;
            RebuildSeasonItems(
                latestCompleted.Year,
                latestCompleted.Season);

            YearComboBox.SelectionChanged += OnYearSelectionChanged;
            SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;

            // 触发初始加载
            if (YearComboBox.SelectedItem is int year &&
                SeasonComboBox.SelectedItem is ComboBoxItem item && item.Tag is Season season)
            {
                _ = LoadSeasonAsync(year, season);
            }
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

        private void RebuildSeasonItems(int year, Season? defaultSeason = null)
        {
            _isRebuilding = true;

            // 禁用 ComboBox 后再修改 Items，避免 WinUI 内部处理清除/重建时计算无效 transform
            SeasonComboBox.IsEnabled = false;
            SeasonComboBox.SelectionChanged -= OnSeasonSelectionChanged;
            SeasonComboBox.Items.Clear();

            var allSeasons = new[] { Season.Winter, Season.Spring, Season.Summer, Season.Fall };
            var latestCompleted = GetLatestCompletedSeason(DateTime.Now);
            var maxSeason = defaultSeason
                ?? (year < latestCompleted.Year
                    ? Season.Fall
                    : latestCompleted.Season);
            var validSeasons = year < latestCompleted.Year
                ? allSeasons
                : allSeasons.TakeWhile(s => s <= maxSeason).ToArray();

            foreach (var season in validSeasons)
            {
                SeasonComboBox.Items.Add(new ComboBoxItem
                {
                    Content = season switch
                    {
                        Season.Winter => "冬 (1-3月)",
                        Season.Spring => "春 (4-6月)",
                        Season.Summer => "夏 (7-9月)",
                        Season.Fall => "秋 (10-12月)",
                        _ => season.ToString()
                    },
                    Tag = season
                });
            }

            // 选中默认季度
            for (int i = 0; i < SeasonComboBox.Items.Count; i++)
            {
                if (((ComboBoxItem)SeasonComboBox.Items[i]).Tag is Season s && s == maxSeason)
                {
                    SeasonComboBox.SelectedIndex = i;
                    SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;
                    SeasonComboBox.IsEnabled = true;
                    _isRebuilding = false;
                    return;
                }
            }
            SeasonComboBox.SelectedIndex = SeasonComboBox.Items.Count - 1;
            SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;
            SeasonComboBox.IsEnabled = true;
            _isRebuilding = false;
        }

        private async Task LoadSeasonAsync(int year, Season season)
        {
            // 立即显示加载覆盖层（不依赖 PropertyChanged 的异步回调延迟）
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;
            LoadingFailedImage.Visibility = Visibility.Collapsed;
            LoadingHint.Text = "加载中…";

            // 取消上一轮请求
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var version = Interlocked.Increment(ref _loadVersion);

            await ViewModel.LoadPastSeasonAnimeAsync(year, season, _loadCts.Token);

            // 如果已有更新的请求，丢弃此结果（此时 IsLoading 可能已被旧请求设为 false）
            if (version != _loadVersion) return;
            UpdateViewState();

            // 数据加载完成，隐藏覆盖层
            LoadingOverlay.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = false;
        }

        private void OnYearSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (YearComboBox.SelectedItem is not int year) return;
            RebuildSeasonItems(year);

            // 年份变更后立即加载新季度数据
            if (SeasonComboBox.SelectedItem is ComboBoxItem item && item.Tag is Season season)
            {
                _ = LoadSeasonAsync(year, season);
            }
        }

        private async void OnSeasonSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 组合框重建期间的穿透事件被忽略，避免级联取消
            if (_isRebuilding) return;
            if (YearComboBox.SelectedItem is not int year) return;
            if (SeasonComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not Season season) return;
            await LoadSeasonAsync(year, season);
        }

        private void OnAnimeCardClicked(object? sender, Views.Controls.AnimeCardClickedEventArgs e)
        {
            _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);
        }

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
                System.Diagnostics.Debug.WriteLine($"[PastSeasonPage] LoadDragConfigAndBlockedAsync failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        // ======== 自定义拖放 ========

        private IDisposable? _dropHostRegistration;

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
        }

        private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
        {
            _dropHostRegistration?.Dispose();
            _dropHostRegistration = null;
        }


        private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var result = FindChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

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
            ViewModel.AnimeList.Clear();
            foreach (var anime in filtered)
            {
                ViewModel.AnimeList.Add(anime);
            }
        }
    }
}
