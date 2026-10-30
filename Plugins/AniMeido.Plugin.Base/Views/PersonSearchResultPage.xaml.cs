using System.ComponentModel;
using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class PersonSearchResultPage : Page, INavigationAware
{
    private readonly PersonSearchResultsViewModel _viewModel;
    private readonly IPluginNavigator _pluginNavigator;
    private readonly DragDropService _dragDrop;
    private readonly ILogger<PersonSearchResultPage> _logger;
    private IDisposable? _dropHostRegistration;
    private Task? _activationTask;

    public PersonSearchResultPage(
        IAnimeDataSource dataSource,
        TrackingService tracking,
        DragDropService dragDropService,
        IPluginNavigator pluginNavigator,
        ILogger<PersonSearchResultPage> logger)
    {
        _viewModel = new PersonSearchResultsViewModel(dataSource, tracking, logger);
        _dragDrop = dragDropService;
        _pluginNavigator = pluginNavigator;
        _logger = logger;
        InitializeComponent();
        ResultGrid.ItemsSource = _viewModel.Results;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is (int personId, string personName))
            return _activationTask = ObserveActivationAsync(_viewModel.LoadAsync(personId, personName));

        _viewModel.CancelLoad();
        _activationTask = null;
        return Task.CompletedTask;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PersonSearchResultsViewModel.Title):
                TitleBlock.Text = _viewModel.Title;
                break;
            case nameof(PersonSearchResultsViewModel.ResultText):
                ResultCount.Text = _viewModel.ResultText;
                break;
            case nameof(PersonSearchResultsViewModel.IsLoading):
                LoadingOverlay.Visibility = _viewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                LoadingRing.IsActive = _viewModel.IsLoading;
                break;
        }
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid rootGrid)
            return;

        _dropHostRegistration?.Dispose();
        _dropHostRegistration = _dragDrop.AttachStandardDragHost(
            rootGrid,
            DragOverlay,
            DragAction.PlanToWatch);
        rootGrid.Unloaded -= OnRootGridUnloaded;
        rootGrid.Unloaded += OnRootGridUnloaded;

        if (_activationTask is { IsCompleted: false })
            return;

        _activationTask = ObserveActivationAsync(_viewModel.ResumeAsync());
        await _activationTask;
    }

    private async Task ObserveActivationAsync(Task activation)
    {
        try
        {
            await activation;
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // 页面统一观察激活任务，初次导航和 Loaded 不重复报告同一次失败。
        catch (Exception ex)
        {
            _logger.LogError(ex, "Person search page failed to load.");
        }
#pragma warning restore CA1031
    }

    private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelLoad();
        _activationTask = null;
        _dropHostRegistration?.Dispose();
        _dropHostRegistration = null;
    }

    private void OnAnimeCardClicked(object? sender, Controls.AnimeCardClickedEventArgs e)
        => _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);
}
