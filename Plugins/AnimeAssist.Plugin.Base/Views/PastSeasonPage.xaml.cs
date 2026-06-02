using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class PastSeasonPage : Page
    {
        private readonly PastSeasonViewModel _viewModel;
        public PastSeasonViewModel ViewModel => _viewModel;
        private readonly List<Anime> _allAnime = new();
        private HashSet<int> _blockedIds = new();
        private DragDropService _dragDrop;
        private TrackingService _tracking;

        public PastSeasonPage(IAnimeDataSource dataSource, DragDropService dragDropService, TrackingService trackingService)
        {
            _viewModel = new PastSeasonViewModel(dataSource);
            _dragDrop = dragDropService;
            _tracking = trackingService;
            InitializeComponent();

            _ = LoadDragConfigAndBlockedAsync();

            ViewModel.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(PastSeasonViewModel.IsLoading):
                        UpdateOverlayState();
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
                            _allAnime.AddRange(ViewModel.AnimeList.Where(a => !_blockedIds.Contains(a.ID)));
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
                ErrorInfoBar.Visibility = Visibility.Visible;
                EmptyState.Visibility = Visibility.Collapsed;
            }
            else
            {
                ErrorInfoBar.IsOpen = false;
                ErrorInfoBar.Visibility = Visibility.Collapsed;
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
            int currentYear = DateTime.Now.Year;
            for (int y = 2000; y <= currentYear; y++)
                YearComboBox.Items.Add(y);

            // 计算上一个季度及对应的年份
            var previousSeason = GetPreviousSeason();
            var currentSeason = GetCurrentSeason();
            var previousYear = currentYear;
            if (currentSeason == Season.Winter && previousSeason == Season.Fall)
                previousYear--;

            // 默认识别到上一个年度的最后一个季度之前的季度
            var previousYear2 = currentSeason switch
            {
                Season.Winter => currentYear,
                _ => currentYear
            };

            YearComboBox.SelectedItem = previousYear;
            RebuildSeasonItems(previousYear, previousSeason);

            YearComboBox.SelectionChanged += OnYearSelectionChanged;
            SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;

            // 触发初始加载
            if (YearComboBox.SelectedItem is int year &&
                SeasonComboBox.SelectedItem is ComboBoxItem item && item.Tag is Season season)
            {
                _ = ViewModel.LoadPastSeasonAnimeAsync(year, season);
            }
        }

        private static Season GetCurrentSeason()
        {
            return DateTime.Now.Month switch
            {
                >= 1 and <= 3 => Season.Winter,
                >= 4 and <= 6 => Season.Spring,
                >= 7 and <= 9 => Season.Summer,
                _ => Season.Fall
            };
        }

        private static Season GetPreviousSeason()
        {
            var current = DateTime.Now.Month switch
            {
                >= 1 and <= 3 => Season.Winter,
                >= 4 and <= 6 => Season.Spring,
                >= 7 and <= 9 => Season.Summer,
                _ => Season.Fall
            };
            return current switch
            {
                Season.Winter => Season.Fall,
                Season.Spring => Season.Winter,
                Season.Summer => Season.Spring,
                Season.Fall => Season.Summer,
                _ => Season.Winter
            };
        }

        private void RebuildSeasonItems(int year, Season? defaultSeason = null)
        {
            SeasonComboBox.SelectionChanged -= OnSeasonSelectionChanged;
            SeasonComboBox.Items.Clear();

            var allSeasons = new[] { Season.Winter, Season.Spring, Season.Summer, Season.Fall };
            var maxSeason = defaultSeason ?? GetPreviousSeason();
            var validSeasons = year < DateTime.Now.Year
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
                    return;
                }
            }
            SeasonComboBox.SelectedIndex = SeasonComboBox.Items.Count - 1;
            SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;
        }

        private void OnYearSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (YearComboBox.SelectedItem is not int year) return;
            RebuildSeasonItems(year);

            // 年份变更后立即加载新季度数据
            if (SeasonComboBox.SelectedItem is ComboBoxItem item && item.Tag is Season season)
            {
                _ = ViewModel.LoadPastSeasonAnimeAsync(year, season);
            }
        }

        private async void OnSeasonSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (YearComboBox.SelectedItem is not int year) return;
            if (SeasonComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not Season season) return;
            await ViewModel.LoadPastSeasonAnimeAsync(year, season);
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
        }

        private async Task LoadDragConfigAndBlockedAsync()
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

        // ======== 自定义拖放 ========

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            var rootGrid = (Grid)sender;
            rootGrid.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnCapturedPointerPressed), true);
        }

        private void OnCapturedPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _dragDrop.HandlePointerPressed(this, e);
        }

        private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            _dragDrop.HandlePointerMoved(this, DragOverlay, e, DragAction.Watching);
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
                if (_dragDrop.DragGhost != null)
                    DragOverlay.Children.Remove(_dragDrop.DragGhost);
                foreach (var zone in _dragDrop.ActiveZones)
                    DragOverlay.Children.Remove(zone.Border);
                DragOverlay.Visibility = Visibility.Collapsed;
            }
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
            if (string.IsNullOrWhiteSpace(query))
            {
                ViewModel.AnimeList.Clear();
                foreach (var a in _allAnime)
                    ViewModel.AnimeList.Add(a);
                return;
            }

            var lower = query.ToLowerInvariant();
            var filtered = _allAnime
                .Where(a => a.Title.Contains(lower, StringComparison.OrdinalIgnoreCase))
                .ToList();

            ViewModel.AnimeList.Clear();
            foreach (var a in filtered)
                ViewModel.AnimeList.Add(a);
        }
    }
}