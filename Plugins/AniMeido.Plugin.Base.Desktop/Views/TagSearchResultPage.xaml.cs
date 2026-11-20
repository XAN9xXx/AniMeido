using System.ComponentModel;
using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class TagSearchResultPage : Page, INavigationAware
{
    private readonly DragDropService _dragDrop;
    private readonly SavedTagService _savedTags;
    private readonly IPluginNavigator _navigator;
    private readonly ILogger<TagSearchResultPage> _logger;
    private IDisposable? _dropHost;
    private string? _tag;
    private int _visit;
    private int _request;
    private int _favoriteVersion;
    private bool _active;
    private bool _syncing = true;
    private bool _isSaved;
    private bool _savingFavorite;
    private bool _readingFavorite;
    private Task _favoriteWrite = Task.CompletedTask;

    public TagSearchResultsViewModel ViewModel { get; }
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;
    internal Task PendingFavorite { get; private set; } = Task.CompletedTask;

    public TagSearchResultPage(DragDropService dragDropService, TagSearchResults search,
        TrackingService trackingService, SavedTagService savedTagService, IPluginNavigator pluginNavigator,
        ILogger<TagSearchResultPage> logger)
        : this(new TagSearchResultsViewModel(search, trackingService, logger), dragDropService, savedTagService, pluginNavigator, logger)
    {
    }

    internal TagSearchResultPage(TagSearchResultsViewModel viewModel, DragDropService dragDropService,
        SavedTagService savedTagService, IPluginNavigator pluginNavigator, ILogger<TagSearchResultPage> logger)
    {
        _dragDrop = dragDropService;
        _savedTags = savedTagService;
        _navigator = pluginNavigator;
        _logger = logger;
        ViewModel = viewModel;
        InitializeComponent();
        ResultGrid.ItemsSource = ViewModel.Items;
        var year = DateTime.Now.Year;
        for (var value = year; value >= 1960; --value)
        {
            YearFromCombo.Items.Add(new ComboBoxItem { Content = value.ToString(), Tag = value });
            YearToCombo.Items.Add(new ComboBoxItem { Content = value.ToString(), Tag = value });
        }
        ResetControls();
        _syncing = false;
        UpdatePresentation();
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is not string tag || string.IsNullOrWhiteSpace(tag)) return Task.CompletedTask;
        if (string.Equals(_tag, tag, StringComparison.Ordinal)) return PendingActivation;
        ++_visit;
        ViewModel.Suspend();
        _tag = tag;
        TagTitle.Text = tag;
        ResetControls();
        PendingFavorite = RefreshFavoriteAsync();
        PendingActivation = ObserveAsync(() => ViewModel.InitializeAsync(tag, DateTime.Now.Year - 3, DateTime.Now.Year), "加载标签作品", applySort: true);
        return PendingActivation;
    }

    private void ResetControls()
    {
        _syncing = true;
        SortSelector.SelectedIndex = 0;
        SortOrderToggle.IsChecked = true;
        SortOrderToggle.Content = "↓";
        IncludeLowVotesCheckBox.IsChecked = false;
        IncludeLowVotesCheckBox.IsEnabled = true;
        YearFromCombo.SelectedItem = YearFromCombo.Items.OfType<ComboBoxItem>().First(item => (int)item.Tag == DateTime.Now.Year - 3);
        YearToCombo.SelectedIndex = 0;
        _syncing = false;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        if (_active) return;
        _active = true;
        _dropHost = _dragDrop.AttachStandardDragHost(RootGrid, DragOverlay, DragAction.PlanToWatch);
        ViewModel.PropertyChanged += OnViewModelChanged;
        UpdatePresentation();
        if (_tag is not null) PendingFavorite = RefreshFavoriteAsync();
        if (PendingActivation.IsCompleted) PendingActivation = ObserveAsync(ViewModel.ResumeAsync, "恢复标签作品");
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs args)
    {
        _active = false;
        ++_visit;
        ++_favoriteVersion;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Suspend();
        PendingActivation = Task.CompletedTask;
        LoadingRing.IsActive = false;
        _dropHost?.Dispose();
        _dropHost = null;
    }

    private async Task ObserveAsync(Func<Task> operation, string name, bool applySort = false)
    {
        var visit = _visit;
        var request = ++_request;
        try { await operation(); }
        catch (OperationCanceledException) { }
#pragma warning disable CA1031 // 页面接线层处理业务加载之外的异常，并保留筛选控件。
        catch (Exception error)
        {
            if (visit != _visit || request != _request) return;
            _logger.LogError(error, "Tag page failed to {Operation}", name);
            ViewModel.ResultText = "标签作品加载失败，请调整条件后重试。";
        }
#pragma warning restore CA1031
        finally
        {
            if (visit == _visit && request == _request)
            {
                if (applySort) ApplySort();
                UpdatePresentation();
            }
        }
    }

    private async Task RefreshFavoriteAsync()
    {
        var visit = _visit;
        await _favoriteWrite;
        if (visit == _visit) await ReadFavoriteAsync();
    }

    private async Task ReadFavoriteAsync()
    {
        if (_tag is not { } tag) return;
        var version = ++_favoriteVersion;
        _readingFavorite = true;
        FavoriteTagButton.IsEnabled = false;
        try
        {
            var saved = await _savedTags.IsTagSavedAsync(tag);
            if (version != _favoriteVersion) return;
            _isSaved = saved;
            UpdateFavorite();
        }
#pragma warning disable CA1031 // 收藏状态读取失败可在按钮处重试，已有作品列表保留。
        catch (Exception error)
        {
            if (version != _favoriteVersion) return;
            _logger.LogWarning(error, "Saved tag state could not be read for {Tag}", tag);
            FavoriteMessage.Text = "收藏状态读取失败";
            FavoriteTagButton.Content = "重试";
            FavoriteTagButton.Tag = "retry";
        }
#pragma warning restore CA1031
        finally
        {
            if (version == _favoriteVersion)
            {
                _readingFavorite = false;
                FavoriteTagButton.IsEnabled = !_savingFavorite;
            }
        }
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs args)
        => PendingFavorite = FavoriteTagButton.Tag as string == "retry" ? ReadFavoriteAsync() : ToggleFavoriteAsync();

    internal Task ToggleFavoriteAsync()
        => _favoriteWrite = _savingFavorite ? _favoriteWrite : SaveFavoriteCoreAsync();

    private async Task SaveFavoriteCoreAsync()
    {
        if (_savingFavorite || _tag is not { } tag || !FavoriteTagButton.IsEnabled) return;
        _savingFavorite = true;
        var next = !_isSaved;
        var version = ++_favoriteVersion;
        FavoriteTagButton.IsEnabled = false;
        FavoriteMessage.Text = string.Empty;
        try
        {
            if (next) await _savedTags.SaveTagAsync(tag); else await _savedTags.RemoveTagAsync(tag);
            if (version != _favoriteVersion) return;
            _isSaved = next;
            UpdateFavorite();
        }
#pragma warning disable CA1031 // 已接纳收藏写入完整结束，失败时保留原收藏态。
        catch (Exception error)
        {
            _logger.LogError(error, "Saved tag change failed for {Tag}", tag);
            if (version == _favoriteVersion) FavoriteMessage.Text = "收藏未能更改，请重试。";
        }
#pragma warning restore CA1031
        finally
        {
            _savingFavorite = false;
            if (_active && !_readingFavorite) FavoriteTagButton.IsEnabled = true;
        }
    }

    private void UpdateFavorite()
    {
        FavoriteTagButton.Content = _isSaved ? "已收藏" : "收藏标签";
        FavoriteTagButton.Tag = null;
        FavoriteMessage.Text = string.Empty;
    }

    private void OnYearRangeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncing || _tag is null || YearFromCombo.SelectedItem is not ComboBoxItem { Tag: int from }
            || YearToCombo.SelectedItem is not ComboBoxItem { Tag: int to }) return;
        PendingActivation = ObserveAsync(() => ViewModel.ChangeYearRangeAsync(from, to), "更改年份", applySort: true);
    }
    private void OnIncludeLowVotesChanged(object sender, RoutedEventArgs args)
    {
        if (!_syncing && _tag is not null)
            PendingActivation = ObserveAsync(() => ViewModel.SetIncludeLowVotesAsync(IncludeLowVotesCheckBox.IsChecked == true), "更改评分范围", applySort: true);
    }
    private void OnSortChanged(object sender, SelectionChangedEventArgs args) => ApplySort();
    private void OnSortOrderToggle(object sender, RoutedEventArgs args) => ApplySort();
    private void ApplySort()
    {
        if (_syncing || SortSelector.SelectedItem is not ComboBoxItem { Tag: string sort }) return;
        var descending = SortOrderToggle.IsChecked == true;
        SortOrderToggle.Content = descending ? "↓" : "↑";
        IncludeLowVotesCheckBox.IsEnabled = sort == "rank";
        ViewModel.SetSort(sort, descending);
    }
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args) => UpdatePresentation();
    private void UpdatePresentation()
    {
        ResultCount.Text = ViewModel.ResultText;
        PageInfo.Text = ViewModel.PageText;
        PaginationBar.Visibility = ViewModel.HasPages ? Visibility.Visible : Visibility.Collapsed;
        LoadingOverlay.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = _active && ViewModel.IsLoading;
        PrevButton.IsEnabled = ViewModel.CanGoPrevious;
        NextButton.IsEnabled = ViewModel.CanGoNext;
    }
    private void OnPrevPage(object sender, RoutedEventArgs args) => ViewModel.PreviousPage();
    private void OnNextPage(object sender, RoutedEventArgs args) => ViewModel.NextPage();
    private void OnAnimeCardClicked(object? sender, AnimeCardClickedEventArgs args)
        => _navigator.Navigate(typeof(AnimeDetailPage), args.Anime.ID);
}
