using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class PersonSearchResultPage : Page, INavigationAware
{
    private readonly DragDropService _dragDrop;
    private readonly IPluginNavigator _navigator;
    private readonly ILogger<PersonSearchResultPage> _logger;
    private readonly ObservableCollection<DesktopPersonRow> _rows = [];
    private IDisposable? _dropHost;
    private int? _personId;
    private int _visit;
    private int _columns = 1;
    private double _scrollOffset;
    private bool _active;
    private bool _rowsQueued;
    private bool _restoreScroll;

    public PersonSearchResultsViewModel ViewModel { get; }
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;

    public PersonSearchResultPage(IAnimeDataSource dataSource, TrackingService tracking,
        DragDropService dragDropService, IPluginNavigator pluginNavigator, ILogger<PersonSearchResultPage> logger)
    {
        _dragDrop = dragDropService;
        _navigator = pluginNavigator;
        _logger = logger;
        ViewModel = new(dataSource, tracking, logger);
        InitializeComponent();
        ResultRows.ItemsSource = _rows;
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is not (int personId, string personName)) return Task.CompletedTask;
        if (_personId == personId) return PendingActivation;
        ++_visit;
        ViewModel.CancelLoad();
        _personId = personId;
        _scrollOffset = 0;
        _restoreScroll = true;
        _rows.Clear();
        PendingActivation = ObserveAsync(() => ViewModel.LoadAsync(personId, personName));
        return PendingActivation;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        if (_active) return;
        _active = true;
        _restoreScroll = true;
        _dropHost = _dragDrop.AttachStandardDragHost(RootGrid, DragOverlay, DragAction.PlanToWatch);
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Results.CollectionChanged += OnResultsChanged;
        UpdatePresentation();
        QueueRows();
        if (_personId is not null && PendingActivation.IsCompleted)
            PendingActivation = ObserveAsync(ViewModel.ResumeAsync);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs args)
    {
        _active = false;
        ++_visit;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Results.CollectionChanged -= OnResultsChanged;
        ViewModel.CancelLoad();
        PendingActivation = Task.CompletedTask;
        LoadingRing.IsActive = false;
        _dropHost?.Dispose();
        _dropHost = null;
    }

    private async Task ObserveAsync(Func<Task> operation)
    {
        var visit = _visit;
        try { await operation(); }
        catch (OperationCanceledException) { }
#pragma warning disable CA1031 // 页面负责观察意外失败，业务查询已经处理的失败保持原文。
        catch (Exception error)
        {
            if (visit != _visit) return;
            _logger.LogError(error, "Person works page could not complete its load");
            ViewModel.ResultText = "人物作品加载失败，请重试。";
        }
#pragma warning restore CA1031
        finally
        {
            if (visit == _visit) { UpdatePresentation(); QueueRows(); }
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args) => UpdatePresentation();
    private void UpdatePresentation()
    {
        TitleBlock.Text = ViewModel.Title;
        ResultCount.Text = ViewModel.ResultText;
        LoadingOverlay.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = _active && ViewModel.IsLoading;
    }
    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRows();
    private void OnResultSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var columns = Math.Max(1, (int)Math.Floor((args.NewSize.Width + 16) / 186));
        if (_columns == columns) return;
        _columns = columns;
        _restoreScroll = true;
        QueueRows();
    }
    private void QueueRows()
    {
        if (!_active || _rowsQueued) return;
        _rowsQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _rowsQueued = false;
            if (_active) UpdateRows();
        })) _rowsQueued = false;
    }
    private void UpdateRows()
    {
        var rows = DesktopPersonRow.Create(ViewModel.Results, ViewModel.DesktopWorkSnapshots, _columns);
        for (var index = 0; index < rows.Count; ++index)
        {
            if (index == _rows.Count) _rows.Add(rows[index]);
            else if (_rows[index].YearLabel != rows[index].YearLabel
                || !_rows[index].Works.SequenceEqual(rows[index].Works)) _rows[index] = rows[index];
        }
        while (_rows.Count > rows.Count) _rows.RemoveAt(_rows.Count - 1);
    }
    private void OnResultViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_active && IsLoaded && !_restoreScroll) _scrollOffset = ResultScroller.VerticalOffset;
    }
    private void OnResultLayoutUpdated(object? sender, object args)
    {
        if (!_active || !_restoreScroll || ViewModel.IsLoading || _rowsQueued) return;
        _restoreScroll = false;
        ResultScroller.ChangeView(null, _scrollOffset, null, disableAnimation: true);
    }
    private void OnAnimeCardClicked(object? sender, AnimeCardClickedEventArgs args)
        => _navigator.Navigate(typeof(AnimeDetailPage), args.Anime.ID);
}

public sealed record DesktopPersonWork(Anime Anime, string Role);

public sealed record DesktopPersonRow(string? YearLabel, IReadOnlyList<DesktopPersonWork> Works)
{
    public bool HasYearLabel => YearLabel is not null;

    internal static IReadOnlyList<DesktopPersonRow> Create(IReadOnlyList<Anime> anime,
        IReadOnlyList<PersonWork> snapshots, int columns)
    {
        var roles = snapshots.ToDictionary(work => work.ID);
        var rows = new List<DesktopPersonRow>();
        foreach (var year in anime.GroupBy(item => item.AirDate?.Year ?? roles.GetValueOrDefault(item.ID)?.AirDate?.Year)
                     .OrderByDescending(group => group.Key ?? int.MinValue))
        {
            var first = true;
            foreach (var chunk in year.Chunk(Math.Max(1, columns)))
            {
                rows.Add(new(first ? year.Key?.ToString(CultureInfo.InvariantCulture) ?? "日期未知" : null,
                    chunk.Select(item => new DesktopPersonWork(item, roles.GetValueOrDefault(item.ID)?.Staff ?? string.Empty)).ToArray()));
                first = false;
            }
        }
        return rows;
    }
}
