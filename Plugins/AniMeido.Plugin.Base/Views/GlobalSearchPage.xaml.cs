using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AniMeido.Plugin.Base.Views
{
    /// <summary>
    /// 搜索：先列出“我的番剧”里匹配的作品，再列 Bangumi 结果；未搜索时显示最近搜索与收藏的标签。
    /// </summary>
    public sealed partial class GlobalSearchPage : Page
    {
        private readonly DragDropService _dragDrop;
        private readonly IPluginNavigator _pluginNavigator;
        private readonly ILogger<GlobalSearchPage> _logger;
        private IDisposable? _dropHostRegistration;
        private int _loadVersion;

        public GlobalSearchViewModel ViewModel { get; }

        public GlobalSearchPage(
            DragDropService dragDropService,
            IAnimeDataSource dataSource,
            TrackingService trackingService,
            PersonalFeatureStateStore featureState,
            LocalSearchService localSearchService,
            SavedTagService savedTagService,
            IPluginNavigator pluginNavigator,
            ILogger<GlobalSearchPage> logger)
        {
            _dragDrop = dragDropService;
            _pluginNavigator = pluginNavigator;
            _logger = logger;
            ViewModel = new GlobalSearchViewModel(
                dataSource,
                trackingService,
                featureState,
                localSearchService,
                savedTagService,
                logger);
            InitializeComponent();
            InitializeSortControls();
        }

        public static Visibility NoLandingVisibility(bool hasLandingContent)
            => hasLandingContent ? Visibility.Collapsed : Visibility.Visible;

        public static bool NotLoadingMore(bool isLoadingMore) => !isLoadingMore;

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            var version = ++_loadVersion;
            _dropHostRegistration?.Dispose();
            _dropHostRegistration = _dragDrop.AttachStandardDragHost(
                RootGrid,
                DragOverlay,
                DragAction.PlanToWatch);

            RootGrid.Unloaded -= OnRootGridUnloaded;
            RootGrid.Unloaded += OnRootGridUnloaded;

            // 返回页面时：刷新首页内容，并按最新追番状态更新结果（刚屏蔽的会移除）。
            await RunSafelyAsync(ViewModel.LoadLandingAsync, "load landing");
            if (version != _loadVersion)
                return;
            await RunSafelyAsync(ViewModel.RefreshStatusesAsync, "refresh statuses");
            if (version != _loadVersion)
                return;
            await RunSafelyAsync(ViewModel.ResumePendingSearchAsync, "resume search");
        }

        private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
        {
            ++_loadVersion;
            ViewModel.CancelPendingSearch();
            _dropHostRegistration?.Dispose();
            _dropHostRegistration = null;
        }

        /// <summary>页面操作的统一出口：取消静默，其余错误显示在结果区并记 Error，页面继续可用。</summary>
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
                _logger.LogError(ex, "Search page failed to {Operation}", operation);
                ViewModel.ErrorMessage = $"操作失败：{ex.Message}";
                ViewModel.IsError = true;
            }
#pragma warning restore CA1031
        }

        private async void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
            => await SearchAsync(sender.Text);

        private async void OnSearchClick(object sender, RoutedEventArgs e)
            => await SearchAsync(SearchBox.Text);

        private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            // 清空搜索框即回到首页（最近搜索与收藏的标签）。
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput
                && string.IsNullOrWhiteSpace(sender.Text))
            {
                ViewModel.ShowLanding();
            }
        }

        private Task SearchAsync(string? text)
        {
            var query = text?.Trim() ?? "";
            return query.Length == 0
                ? Task.CompletedTask
                : RunSafelyAsync(() => ViewModel.SearchAsync(query), "search");
        }

        private async void OnRecentSearchClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string query })
            {
                SearchBox.Text = query;
                await SearchAsync(query);
            }
        }

        private async void OnClearRecentClick(object sender, RoutedEventArgs e)
            => await RunSafelyAsync(ViewModel.ClearRecentSearchesAsync, "clear recent searches");

        private async void OnRetryClick(object sender, RoutedEventArgs e)
            => await SearchAsync(ViewModel.Query);

        private async void OnLoadMoreClick(object sender, RoutedEventArgs e)
            => await RunSafelyAsync(ViewModel.LoadMoreAsync, "load more");

        private void InitializeSortControls()
        {
            foreach (var (key, label) in new[]
            {
                (SearchSortKey.Match, "匹配度"),
                (SearchSortKey.Score, "评分"),
                (SearchSortKey.AirDate, "开播日期"),
            })
            {
                SortComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            }

            SortComboBox.SelectedIndex = 0;
            SortComboBox.SelectionChanged += (_, _) =>
            {
                if (SortComboBox.SelectedItem is ComboBoxItem { Tag: SearchSortKey key })
                    ViewModel.SetSortKey(key);
            };
        }

        private void OnFormatChipClick(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton { Tag: PastSeasonFormatChip chip } button)
                return;

            // 再点已选中的标签不取消选中，保持“始终选中一项”。
            if (chip.IsSelected)
            {
                button.IsChecked = true;
                return;
            }

            ViewModel.SelectFormat(chip.Format);
        }

        private void OnOpenMineClick(object sender, RoutedEventArgs e)
            => _pluginNavigator.Navigate(typeof(ManagementPage), ViewModel.Query);

        private void OnSavedTagClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag })
                _pluginNavigator.Navigate(typeof(TagSearchResultPage), tag);
        }

        private void OnLocalMatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: PastSeasonEntry entry })
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), entry.Anime.ID);
        }

        private void OnAnimeCardClicked(object? sender, AnimeCardClickedEventArgs e)
            => _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);

        private void OnLocalMatchContainerContentChanging(
            ListViewBase sender,
            ContainerContentChangingEventArgs args)
        {
            if (!args.InRecycleQueue && args.Phase == 0)
            {
                args.RegisterUpdateCallback(OnLocalMatchContainerContentChanging);
                return;
            }

            if (args.ItemContainer?.ContentTemplateRoot is not FrameworkElement root
                || root.FindName("LocalMatchCover") is not Image image)
            {
                return;
            }

            // 回收不会保证触发 Unloaded；容器在这里交还旧封面请求的所有权。
            if (args.InRecycleQueue)
            {
                ManagedImageLoader.Cancel(image);
                return;
            }

            if (args.Item is PastSeasonEntry { Anime: var anime })
                ManagedImageLoader.ConfigureCover(image, anime.ID, anime.CoverURL, 40);
        }
    }
}
