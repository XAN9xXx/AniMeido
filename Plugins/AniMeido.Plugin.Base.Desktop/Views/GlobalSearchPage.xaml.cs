using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class GlobalSearchPage : Page
{
    private readonly DragDropService _dragDrop;
    private readonly IPluginNavigator _navigator;
    private readonly ILogger<GlobalSearchPage> _logger;
    private IDisposable? _dropHost;
    private int _visit;
    private int _action;
    private bool _active;
    private bool _syncing = true;

    public GlobalSearchViewModel ViewModel { get; }
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;

    public GlobalSearchPage(DragDropService dragDropService, IAnimeDataSource dataSource,
        TrackingService trackingService, PersonalFeatureStateStore featureState, LocalSearchService localSearchService,
        SavedTagService savedTagService, IPluginNavigator pluginNavigator, ILogger<GlobalSearchPage> logger)
    {
        _dragDrop = dragDropService;
        _navigator = pluginNavigator;
        _logger = logger;
        ViewModel = new(dataSource, trackingService, featureState, localSearchService, savedTagService, logger);
        InitializeComponent();
        foreach (var (key, text) in new[]
        {
            (SearchSortKey.Match, "相关性"), (SearchSortKey.Score, "评分"), (SearchSortKey.AirDate, "开播日期"),
        }) SortComboBox.Items.Add(new ComboBoxItem { Content = text, Tag = key });
        SyncControls();
        SortComboBox.SelectionChanged += OnSortChanged;
    }

    public static Visibility NoLandingVisibility(bool hasContent) => hasContent ? Visibility.Collapsed : Visibility.Visible;
    public static bool NotLoadingMore(bool loading) => !loading;

    private void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        if (_active) return;
        _active = true;
        var visit = ++_visit;
        var activation = _action + 1;
        _dropHost = _dragDrop.AttachStandardDragHost(RootGrid, DragOverlay, DragAction.PlanToWatch);
        PendingActivation = ObserveAsync(async () =>
        {
            await _dragDrop.ReloadConfigAsync();
            if (!Current(visit) || activation != _action) return;
            await ViewModel.LoadLandingAsync();
            if (!Current(visit) || activation != _action) return;
            await ViewModel.RefreshStatusesAsync();
            if (Current(visit) && activation == _action) await ViewModel.ResumePendingSearchAsync();
        }, "恢复搜索");
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs args)
    {
        _active = false;
        ++_visit;
        ++_action;
        ViewModel.CancelPendingSearch();
        _dropHost?.Dispose();
        _dropHost = null;
    }

    private bool Current(int visit) => _active && visit == _visit;

    private async Task ObserveAsync(Func<Task> action, string name)
    {
        var visit = _visit;
        var request = ++_action;
        try
        {
            await action();
            if (Current(visit) && request == _action) SyncControls();
        }
        catch (OperationCanceledException) { }
#pragma warning disable CA1031 // 页面接纳的操作统一报告；旧访问的结果由各自的代次作废。
        catch (Exception error)
        {
            if (!Current(visit) || request != _action) return;
            _logger.LogError(error, "Search page failed to {Operation}", name);
            ViewModel.ErrorMessage = "搜索操作失败，请重试。";
            ViewModel.IsError = true;
        }
#pragma warning restore CA1031
    }

    internal Task SearchAsync(string? text)
    {
        var query = text?.Trim() ?? string.Empty;
        if (!_active || query.Length == 0) return Task.CompletedTask;
        return ObserveAsync(() => ViewModel.SearchAsync(query), "搜索作品");
    }

    private async void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => await SearchAsync(sender.Text);
    private async void OnSearchClick(object sender, RoutedEventArgs args) => await SearchAsync(SearchBox.Text);
    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_syncing || !string.IsNullOrWhiteSpace(sender.Text)) return;
        ++_action;
        ViewModel.ShowLanding();
    }
    private async void OnRecentSearchClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string query }) return;
        SearchBox.Text = query;
        await SearchAsync(query);
    }
    private async void OnClearRecentClick(object sender, RoutedEventArgs args)
        => await ObserveAsync(ViewModel.ClearRecentSearchesAsync, "清除最近搜索");
    private async void OnRetryClick(object sender, RoutedEventArgs args) => await SearchAsync(ViewModel.Query);
    private async void OnLoadMoreClick(object sender, RoutedEventArgs args)
        => await ObserveAsync(ViewModel.LoadMoreAsync, "加载更多作品");
    private async void OnSortChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncing && SortComboBox.SelectedItem is ComboBoxItem { Tag: SearchSortKey key })
            await ObserveAsync(() => ViewModel.SetSortAsync(key), "更改排序");
    }
    private async void OnFormatChipClick(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton { Tag: PastSeasonFormatChip chip } button) return;
        button.IsChecked = true;
        if (!chip.IsSelected) await ObserveAsync(() => ViewModel.SelectFormatAsync(chip.Format), "筛选作品");
    }
    private async void OnIncludeLowVotesChanged(object sender, RoutedEventArgs args)
    {
        if (!_syncing)
            await ObserveAsync(() => ViewModel.SetIncludeLowVotesAsync(IncludeLowVotes.IsChecked == true), "更改评分范围");
    }
    private void SyncControls()
    {
        _syncing = true;
        SortComboBox.SelectedItem = SortComboBox.Items.OfType<ComboBoxItem>()
            .First(item => (SearchSortKey)item.Tag == ViewModel.SortKey);
        IncludeLowVotes.IsChecked = ViewModel.IncludeLowVotes;
        IncludeLowVotes.IsEnabled = ViewModel.SortKey == SearchSortKey.Score;
        _syncing = false;
    }
    private void OnOpenMineClick(object sender, RoutedEventArgs args)
        => _navigator.Navigate(typeof(ManagementPage), ViewModel.Query);
    private void OnSavedTagClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string tag }) _navigator.Navigate(typeof(TagSearchResultPage), tag);
    }
    private void OnLocalMatchClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: PastSeasonEntry entry }) _navigator.Navigate(typeof(AnimeDetailPage), entry.Anime.ID);
    }
    private void OnAnimeCardClicked(object? sender, AnimeCardClickedEventArgs args)
        => _navigator.Navigate(typeof(AnimeDetailPage), args.Anime.ID);

    private void OnLocalCoverLoaded(object sender, RoutedEventArgs args) => UpdateLocalCover((Image)sender);
    private void OnLocalCoverChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        // Uno 在复用模板时先变更上下文；下一次调度读取已经更新的作品。
        sender.DispatcherQueue.TryEnqueue(() => { if (sender.IsLoaded) UpdateLocalCover((Image)sender); });
    }
    private static void UpdateLocalCover(Image image)
    {
        if (image.DataContext is PastSeasonEntry { Anime: var anime })
            ManagedImageLoader.ConfigureCover(image, anime.ID, anime.CoverURL, 40);
        else ManagedImageLoader.Cancel(image);
    }
}
