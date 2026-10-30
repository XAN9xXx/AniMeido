using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class TagSearchResultPage : Page, INavigationAware
{
    private const int YearStart = 1960;
    private readonly IPluginNavigator _pluginNavigator;
    private readonly DragDropService _dragDrop;
    private readonly ILogger<TagSearchResultPage> _logger;
    private Task? _activationTask;
    private IDisposable? _dropHostRegistration;
    private bool _isInitializing = true;

    public TagSearchResultsViewModel ViewModel { get; }

    public TagSearchResultPage(DragDropService dragDropService, TagSearchResults search,
        TrackingService trackingService, IPluginNavigator pluginNavigator,
        ILogger<TagSearchResultPage> logger)
    {
        _dragDrop = dragDropService;
        _pluginNavigator = pluginNavigator;
        _logger = logger;
        ViewModel = new TagSearchResultsViewModel(search, trackingService, logger);
        InitializeComponent();
        _isInitializing = false;
        ResultGrid.ItemsSource = ViewModel.Items;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdatePresentation();
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is not string tag || string.IsNullOrEmpty(tag))
            return Task.CompletedTask;

        TagTitle.Text = tag;
        _isInitializing = true;
        (int From, int To) years;
        try
        {
            SortOrderToggle.IsChecked = true;
            years = InitializeYearControls();
        }
        finally
        {
            _isInitializing = false;
        }
        _activationTask = ObserveAsync(
            ViewModel.InitializeAsync(tag, years.From, years.To),
            "加载结果");
        return _activationTask;
    }

    private (int From, int To) InitializeYearControls()
    {
        var currentYear = DateTime.Now.Year;
        var fromYear = currentYear - 3;
        YearFromCombo.Items.Clear();
        YearToCombo.Items.Clear();
        for (var year = currentYear; year >= YearStart; year--)
        {
            YearFromCombo.Items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year });
            YearToCombo.Items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year });
        }
        YearFromCombo.SelectedItem = YearFromCombo.Items.OfType<ComboBoxItem>()
            .First(item => (int)item.Tag == fromYear);
        YearToCombo.SelectedItem = YearToCombo.Items.OfType<ComboBoxItem>()
            .First(item => (int)item.Tag == currentYear);
        return (fromYear, currentYear);
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _dropHostRegistration?.Dispose();
        _dropHostRegistration = _dragDrop.AttachStandardDragHost(
            RootGrid, DragOverlay, DragAction.PlanToWatch);
        RootGrid.Unloaded -= OnRootGridUnloaded;
        RootGrid.Unloaded += OnRootGridUnloaded;
        if (_activationTask is { IsCompleted: false })
            return;

        _activationTask = ObserveAsync(ViewModel.ResumeAsync(), "恢复结果");
        await _activationTask;
    }

    private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
    {
        _dropHostRegistration?.Dispose();
        _dropHostRegistration = null;
        ViewModel.Suspend();
        _activationTask = null;
    }

    private async void OnYearRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing
            || YearFromCombo.SelectedItem is not ComboBoxItem { Tag: int from }
            || YearToCombo.SelectedItem is not ComboBoxItem { Tag: int to })
        {
            return;
        }
        _activationTask = ObserveAsync(ViewModel.ChangeYearRangeAsync(from, to), "更改年份");
        await _activationTask;
    }

    private async Task ObserveAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // 本页最终观察加载任务；返回已处理结果，导航层不再重复记录。
        catch (Exception ex)
        {
            _logger.LogError(ex, "标签结果页{Operation}失败。", operation);
        }
#pragma warning restore CA1031
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing && SortSelector.SelectedItem is ComboBoxItem { Tag: string sort })
            ViewModel.SetSort(sort, SortOrderToggle.IsChecked == true);
    }

    private void OnSortOrderToggle(object sender, RoutedEventArgs e)
    {
        var descending = SortOrderToggle.IsChecked == true;
        SortOrderToggle.Content = descending ? "↓" : "↑";
        if (SortSelector.SelectedItem is ComboBoxItem { Tag: string sort })
            ViewModel.SetSort(sort, descending);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TagSearchResultsViewModel.ResultText):
                ResultCount.Text = ViewModel.ResultText;
                break;
            case nameof(TagSearchResultsViewModel.PageText):
                PageInfo.Text = ViewModel.PageText;
                break;
            case nameof(TagSearchResultsViewModel.HasPages):
                PaginationBar.Visibility = ViewModel.HasPages ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(TagSearchResultsViewModel.IsLoading):
                LoadingOverlay.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                LoadingRing.IsActive = ViewModel.IsLoading;
                break;
            case nameof(TagSearchResultsViewModel.CanGoPrevious):
                PrevButton.IsEnabled = ViewModel.CanGoPrevious;
                break;
            case nameof(TagSearchResultsViewModel.CanGoNext):
                NextButton.IsEnabled = ViewModel.CanGoNext;
                break;
        }
    }

    private void UpdatePresentation()
    {
        ResultCount.Text = ViewModel.ResultText;
        PageInfo.Text = ViewModel.PageText;
        PaginationBar.Visibility = ViewModel.HasPages ? Visibility.Visible : Visibility.Collapsed;
        LoadingOverlay.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = ViewModel.IsLoading;
        PrevButton.IsEnabled = ViewModel.CanGoPrevious;
        NextButton.IsEnabled = ViewModel.CanGoNext;
    }

    private void OnPrevPage(object sender, RoutedEventArgs e) => ViewModel.PreviousPage();

    private void OnNextPage(object sender, RoutedEventArgs e) => ViewModel.NextPage();

    private void OnAnimeCardClicked(object? sender, Views.Controls.AnimeCardClickedEventArgs e)
        => _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);

}
