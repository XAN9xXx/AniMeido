using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AniMeido.Plugin.Base.Views
{
    /// <summary>
    /// 我的番剧：按状态整理标记过的作品，可跨状态搜索、直接改状态、批量处理，
    /// 并管理收藏的标签。带字符串参数打开时直接搜索该词（搜索页“在我的番剧中查看”）。
    /// </summary>
    public sealed partial class ManagementPage : Page, INavigationAware
    {
        private readonly IPluginNavigator _pluginNavigator;
        private readonly ILogger<ManagementPage> _logger;
        private bool _isBatchMode;
        private bool _isGridLayout;

        public ManagementViewModel ViewModel { get; }

        public ManagementPage(
            TrackingService trackingService,
            IAnimeDataSource dataSource,
            SavedTagService savedTagService,
            LocalSearchService searchService,
            ArchiveService archiveService,
            IPluginNavigator pluginNavigator,
            ILogger<ManagementPage> logger)
        {
            _pluginNavigator = pluginNavigator;
            _logger = logger;
            ViewModel = new(
                trackingService,
                dataSource,
                savedTagService,
                searchService,
                archiveService);
            InitializeComponent();
            InitializeSortControls();
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            Unloaded += OnPageUnloaded;
        }

        public async Task OnNavigatedToAsync(object? parameter)
        {
            if (parameter is not string query || string.IsNullOrWhiteSpace(query))
                return;

            SearchBox.Text = query.Trim();
            ViewModel.PrepareSearch(query);
            // 页面已在显示时 Loaded 不会再触发，这里直接刷新；否则交给 Loaded。
            if (IsLoaded)
                await LoadSafelyAsync();
        }

        public static Visibility EmptyVisibility(bool hasNoEntries, bool isLoading, bool isListView)
            => hasNoEntries && !isLoading && isListView ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility NoTagsVisibility(bool hasTags)
            => hasTags ? Visibility.Collapsed : Visibility.Visible;

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
            => await LoadSafelyAsync();

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
            => ViewModel.CancelPendingLoads();

        private Task LoadSafelyAsync()
            => RunSafelyAsync(() => ViewModel.LoadAsync(), "load");

        /// <summary>页面操作的统一出口：取消静默，其余错误显示在顶部并记 Warning，页面继续可用。</summary>
        private async Task RunSafelyAsync(Func<Task> action, string operation)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
            }
#pragma warning disable CA1031 // UI 事件边界统一显示错误，避免 async void 终止进程。
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "My anime page failed to {Operation}", operation);
                ViewModel.ErrorMessage = $"操作失败：{ex.Message}";
                ViewModel.IsError = true;
            }
#pragma warning restore CA1031
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ManagementViewModel.View):
                    if (ViewModel.View == MineView.Tags)
                        SetBatchMode(false);
                    UpdateLayoutState();
                    break;
                case nameof(ManagementViewModel.IsHiddenExpanded):
                    HiddenChevron.Glyph = ViewModel.IsHiddenExpanded ? "\uE70E" : "\uE70D";
                    break;
            }
        }

        private void OnErrorInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
            => ViewModel.IsError = false;

        // ======== 导航 ========

        private async void OnStatusNavClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: TrackingStatusSection section })
                return;

            SearchBox.Text = "";
            await RunSafelyAsync(() => ViewModel.SelectSectionAsync(section), "select status");
        }

        private void OnHiddenToggleClick(object sender, RoutedEventArgs e)
            => ViewModel.IsHiddenExpanded = !ViewModel.IsHiddenExpanded;

        private async void OnTagsNavClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            await RunSafelyAsync(ViewModel.ShowTagsAsync, "show tags");
        }

        // ======== 搜索与排序 ========

        private async void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var query = sender.Text?.Trim() ?? "";
            if (query.Length == 0)
                await RunSafelyAsync(ViewModel.ClearSearchAsync, "clear search");
            else
                await RunSafelyAsync(() => ViewModel.SearchAsync(query), "search");
        }

        private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            // 清空搜索框即回到原来的状态列表。
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput
                && string.IsNullOrWhiteSpace(sender.Text))
            {
                await RunSafelyAsync(ViewModel.ClearSearchAsync, "clear search");
            }
        }

        private void InitializeSortControls()
        {
            foreach (var (key, label) in new[]
            {
                (MineSortKey.RecentlyMarked, "最近标记"),
                (MineSortKey.Title, "标题"),
                (MineSortKey.Score, "Bangumi 评分"),
            })
            {
                SortComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            }

            SortComboBox.SelectedIndex = 0;
            SortComboBox.SelectionChanged += (_, _) =>
            {
                if (SortComboBox.SelectedItem is ComboBoxItem { Tag: MineSortKey key })
                    ViewModel.SetSortKey(key);
            };
        }

        // ======== 列表／网格与批量 ========

        private void OnLayoutClick(object sender, RoutedEventArgs e)
        {
            _isGridLayout = ReferenceEquals(sender, GridLayoutButton);
            UpdateLayoutState();
        }

        private void OnBatchClick(object sender, RoutedEventArgs e)
            => SetBatchMode(BatchButton.IsChecked == true);

        private void SetBatchMode(bool enabled)
        {
            _isBatchMode = enabled;
            BatchButton.IsChecked = enabled;
            BatchButtonText.Text = enabled ? "完成" : "批量选择";
            UpdateLayoutState();
        }

        /// <summary>批量选择只在列表里进行；标签视图没有这些工具。</summary>
        private void UpdateLayoutState()
        {
            var listView = ViewModel.IsStatusOrSearchView;
            var showGrid = listView && _isGridLayout && !_isBatchMode;
            ListLayoutButton.IsChecked = !_isGridLayout;
            GridLayoutButton.IsChecked = _isGridLayout;
            GridLayoutButton.IsEnabled = !_isBatchMode;
            MineList.Visibility = listView && !showGrid ? Visibility.Visible : Visibility.Collapsed;
            MineGrid.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
            BatchBar.Visibility = listView && _isBatchMode ? Visibility.Visible : Visibility.Collapsed;
            MineList.IsItemClickEnabled = !_isBatchMode;
            MineList.SelectionMode = _isBatchMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
            UpdateBatchCount();
        }

        private void OnMineSelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateBatchCount();

        private void UpdateBatchCount()
        {
            var count = _isBatchMode ? MineList.SelectedItems.Count : 0;
            BatchCountText.Text = count > 0 ? $"已选 {count} 部" : "选择要处理的作品";
            BatchMoveButton.IsEnabled = count > 0;
            BatchRemoveButton.IsEnabled = count > 0;
        }

        private void OnSelectAllClick(object sender, RoutedEventArgs e)
            => MineList.SelectAll();

        private void OnBatchMenuOpening(object sender, object e)
        {
            if (sender is MenuFlyout menu)
                FillStatusMenu(menu, current: null, target => ChangeSelectedAsync(target));
        }

        private async void OnBatchRemoveClick(object sender, RoutedEventArgs e)
        {
            var count = MineList.SelectedItems.Count;
            if (count == 0)
                return;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "取消标记",
                Content = $"确定取消这 {count} 部作品的标记吗？它们会从“我的番剧”中移除。",
                PrimaryButtonText = "取消标记",
                CloseButtonText = "保留",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ChangeSelectedAsync(null);
        }

        private Task ChangeSelectedAsync(AnimeTrackingStatus? target)
        {
            var entries = MineList.SelectedItems.OfType<MineEntry>().ToList();
            return ChangeAsync(entries, target);
        }

        // ======== 单部修改状态 ========

        private void OnRowStatusMenuOpening(object sender, object e)
        {
            if (sender is MenuFlyout { Target: FrameworkElement { Tag: MineEntry entry } } menu)
                FillStatusMenu(menu, entry.Status, target => ChangeAsync([entry], target));
        }

        /// <summary>状态菜单：常用状态、不想看到的两个状态，最后是取消标记。当前状态打勾。</summary>
        private void FillStatusMenu(
            MenuFlyout menu,
            AnimeTrackingStatus? current,
            Func<AnimeTrackingStatus?, Task> apply)
        {
            menu.Items.Clear();
            var hiddenStarted = false;
            foreach (var section in ViewModel.StatusSections)
            {
                if (MineBrowse.IsHidden(section.Status) && !hiddenStarted)
                {
                    menu.Items.Add(new MenuFlyoutSeparator());
                    hiddenStarted = true;
                }

                var status = section.Status;
                var item = new ToggleMenuFlyoutItem
                {
                    Text = section.Label,
                    IsChecked = status == current,
                    Icon = new FontIcon { Glyph = section.Glyph },
                };
                item.Click += async (_, _) => await apply(status);
                menu.Items.Add(item);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem
            {
                Text = "取消标记",
                Icon = new FontIcon { Glyph = "\uE738" },
            };
            remove.Click += async (_, _) => await apply(null);
            menu.Items.Add(remove);
        }

        private async Task ChangeAsync(IReadOnlyList<MineEntry> entries, AnimeTrackingStatus? target)
        {
            if (entries.Count == 0)
                return;

            await RunSafelyAsync(async () =>
            {
                var result = await ViewModel.ChangeStatusAsync(entries, target);
                if (result.Failed > 0)
                {
                    _logger.LogWarning(
                        "My anime page failed to change {Failed} of {Total} tracking statuses",
                        result.Failed,
                        entries.Count);
                    ViewModel.ErrorMessage = result.Succeeded > 0
                        ? $"已修改 {result.Succeeded} 部，另有 {result.Failed} 部修改失败，请稍后重试。"
                        : "修改失败，请稍后重试。";
                    ViewModel.IsError = true;
                }
            }, "change status");
        }

        // ======== 收藏的标签 ========

        private async void OnTagChipTapped(object sender, TappedRoutedEventArgs e)
        {
            // 标签上的 × 自己处理点击，不应同时选中这个标签。
            for (var source = e.OriginalSource as DependencyObject;
                 source is not null && !ReferenceEquals(source, sender);
                 source = VisualTreeHelper.GetParent(source))
            {
                if (source is ButtonBase)
                    return;
            }

            if (sender is FrameworkElement { Tag: string tag })
                await RunSafelyAsync(() => ViewModel.SelectTagAsync(tag), "select tag");
        }

        private async void OnRemoveTagClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag })
                await RunSafelyAsync(() => ViewModel.RemoveTagAsync(tag), "remove tag");
        }

        private void OnOpenTagPageClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedTag is { } tag)
                _pluginNavigator.Navigate(typeof(TagSearchResultPage), tag);
        }

        // ======== 打开详情 ========

        private void OnMineItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MineEntry entry)
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), entry.Anime.ID);
        }

        private void OnAnimeCardClicked(object? sender, AnimeCardClickedEventArgs e)
            => _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);

        private void OnManagedCoverLoaded(object sender, RoutedEventArgs e)
            => ConfigureManagedCover(sender as Image);

        private void OnManagedCoverDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
            => ConfigureManagedCover(sender as Image);

        private static void ConfigureManagedCover(Image? image)
        {
            if (image is null)
                return;

            if (image.DataContext is not MineEntry { Anime: var anime })
            {
                ManagedImageLoader.Cancel(image);
                return;
            }

            ManagedImageLoader.ConfigureCover(image, anime.ID, anime.CoverURL, 44);
        }
    }
}
