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
using System.ComponentModel;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ManagementPage : Page, INavigationAware
{
    private readonly IPluginNavigator _navigator;
    private readonly ILogger<ManagementPage> _logger;
    private readonly DesktopPageOperations _operations = new();
    private CancellationTokenSource? _visit;
    private ContentDialog? _dialog;
    private bool _batch;
    private bool _grid = true;

    public ManagementViewModel ViewModel { get; }
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;
    internal Task PendingRead { get; private set; } = Task.CompletedTask;
    internal Task PendingOperation => _operations.Pending;

    public ManagementPage(TrackingService trackingService, IAnimeDataSource dataSource, SavedTagService savedTagService,
        LocalSearchService searchService, ArchiveService archiveService, IPluginNavigator pluginNavigator, ILogger<ManagementPage> logger)
    {
        _navigator = pluginNavigator;
        _logger = logger;
        ViewModel = new(trackingService, dataSource, savedTagService, searchService, archiveService, logger);
        InitializeComponent();
        foreach (var (key, label) in new[] { (MineSortKey.RecentlyMarked, "最近标记"), (MineSortKey.Title, "标题"), (MineSortKey.Score, "评分") })
            SortComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        SortComboBox.SelectedIndex = 0;
        SortComboBox.SelectionChanged += (_, _) =>
        {
            if (SortComboBox.SelectedItem is ComboBoxItem { Tag: MineSortKey sort }) ViewModel.SetSortKey(sort);
        };
        Unloaded += OnPageUnloaded;
        UpdateLayoutState();
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is not string { Length: > 0 } query) return Task.CompletedTask;
        SearchBox.Text = query.Trim();
        ViewModel.PrepareSearch(query);
        return _visit is null ? Task.CompletedTask : PendingActivation = ActivateAsync(_visit.Token);
    }

    private void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        if (_visit is not null) return;
        _visit = new();
        ViewModel.PropertyChanged += OnViewModelChanged;
        PendingActivation = ActivateAsync(_visit.Token);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs args)
    {
        if (_visit is not { } visit) return;
        _visit = null;
        _dialog?.Hide();
        visit.Cancel();
        visit.Dispose();
        ViewModel.CancelPendingLoads();
        ViewModel.PropertyChanged -= OnViewModelChanged;
    }

    private async Task ActivateAsync(CancellationToken token)
    {
        await _operations.DrainAsync();
        if (token.IsCancellationRequested) return;
        await RunSafelyAsync(() => ViewModel.LoadAsync(token), token);
        if (!token.IsCancellationRequested) UpdateLayoutState();
    }

    private void Read(Func<Task> read)
    {
        if (_visit is { } visit) PendingRead = RunSafelyAsync(read, visit.Token);
    }

    private Task Write(Func<Task> write)
    {
        if (_visit is not { } visit) return Task.CompletedTask;
        var operation = RunSafelyAsync(write, visit.Token);
        _operations.Track(operation);
        return operation;
    }

    private async Task RunSafelyAsync(Func<Task> action, CancellationToken token)
    {
        try { await action(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 页面操作的最终提示与日志边界。
        catch (Exception error)
        {
            _logger.LogError(error, "我的番剧操作失败。");
            if (!token.IsCancellationRequested && !DesktopFeedback.TryShow("操作未完成，请重试。", error: true))
            { ViewModel.ErrorMessage = "操作未完成，请重试。"; ViewModel.IsError = true; }
        }
#pragma warning restore CA1031
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ManagementViewModel.View))
        {
            if (ViewModel.IsTagsView) SetBatchMode(false);
            UpdateLayoutState();
        }
        if (args.PropertyName == nameof(ManagementViewModel.IsHiddenExpanded))
            HiddenChevron.Glyph = ViewModel.IsHiddenExpanded ? "\uE70E" : "\uE70D";
    }

    private void OnStatusNavClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: TrackingStatusSection section }) return;
        SearchBox.Text = "";
        Read(() => ViewModel.SelectSectionAsync(section));
    }
    private void OnTagsNavClick(object sender, RoutedEventArgs args) { SearchBox.Text = ""; Read(ViewModel.ShowTagsAsync); }
    private void OnHiddenToggleClick(object sender, RoutedEventArgs args) => ViewModel.IsHiddenExpanded = !ViewModel.IsHiddenExpanded;
    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => Read(() => string.IsNullOrWhiteSpace(sender.Text) ? ViewModel.ClearSearchAsync() : ViewModel.SearchAsync(sender.Text.Trim()));
    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && string.IsNullOrWhiteSpace(sender.Text)) Read(ViewModel.ClearSearchAsync);
    }
    private void OnErrorInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.IsError = false;
    public static Visibility EmptyVisibility(bool empty, bool loading, bool list) => empty && !loading && list ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility NoTagsVisibility(bool hasTags) => hasTags ? Visibility.Collapsed : Visibility.Visible;

    private void OnLayoutClick(object sender, RoutedEventArgs args) { _grid = ReferenceEquals(sender, GridLayoutButton); UpdateLayoutState(); }
    private void OnBatchClick(object sender, RoutedEventArgs args) => SetBatchMode(BatchButton.IsChecked == true);
    internal void SetBatchMode(bool enabled)
    {
        _batch = enabled;
        if (!enabled) MineList.SelectedItems.Clear();
        BatchButton.IsChecked = enabled;
        BatchButtonText.Text = enabled ? "完成" : "批量选择";
        UpdateLayoutState();
    }
    private void UpdateLayoutState()
    {
        var list = ViewModel.IsStatusOrSearchView;
        var grid = list && _grid && !_batch;
        MineGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        MineList.Visibility = list && !grid ? Visibility.Visible : Visibility.Collapsed;
        GridLayoutButton.IsChecked = _grid;
        ListLayoutButton.IsChecked = !_grid;
        GridLayoutButton.IsEnabled = !_batch;
        BatchBar.Visibility = list && _batch ? Visibility.Visible : Visibility.Collapsed;
        MineList.IsItemClickEnabled = !_batch;
        MineList.SelectionMode = _batch ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        UpdateBatchCount();
    }
    private void OnMineSelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateBatchCount();
    private void UpdateBatchCount()
    {
        var count = _batch ? MineList.SelectedItems.Count : 0;
        BatchCountText.Text = count == 0 ? "选择要处理的作品" : $"已选 {count} 部";
        BatchMoveButton.IsEnabled = BatchRemoveButton.IsEnabled = count > 0;
    }
    private void OnSelectAllClick(object sender, RoutedEventArgs args)
    {
        foreach (var item in MineList.Items)
            if (!MineList.SelectedItems.Contains(item)) MineList.SelectedItems.Add(item);
    }
    private void OnBatchMenuOpening(object sender, object args)
    {
        if (sender is MenuFlyout menu) FillStatusMenu(menu, null, target => ChangeEntriesAsync(MineList.SelectedItems.OfType<MineEntry>().ToArray(), target));
    }
    private void OnRowStatusMenuOpening(object sender, object args)
    {
        if (sender is MenuFlyout { Target: FrameworkElement { Tag: MineEntry entry } } menu)
            FillStatusMenu(menu, entry.Status, target => ChangeEntriesAsync([entry], target));
    }
    private void FillStatusMenu(MenuFlyout menu, AnimeTrackingStatus? current, Func<AnimeTrackingStatus?, Task> apply)
    {
        menu.Items.Clear();
        foreach (var section in ViewModel.StatusSections)
        {
            var item = new ToggleMenuFlyoutItem { Text = section.Label, IsChecked = current == section.Status, Icon = new FontIcon { Glyph = section.Glyph } };
            item.Click += (_, _) => _ = apply(section.Status);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem { Text = "取消标记" };
        remove.Click += (_, _) => _ = apply(null);
        menu.Items.Add(remove);
    }
    internal Task ChangeEntriesAsync(IReadOnlyList<MineEntry> entries, AnimeTrackingStatus? target)
    {
        var requested = entries.ToArray();
        if (_visit is not { } visit || requested.Length == 0) return Task.CompletedTask;
        var token = visit.Token;
        return Write(async () =>
        {
            var result = await ViewModel.ChangeStatusAsync(requested, target);
            if (result.Failed == 0) return;
            _logger.LogWarning("我的番剧标记保存失败 {Failed}/{Total}。", result.Failed, requested.Length);
            if (token.IsCancellationRequested) return;
            var message = result.Succeeded > 0 ? $"已修改 {result.Succeeded} 部，另有 {result.Failed} 部保存失败。" : "保存失败，请重试。";
            if (DesktopFeedback.TryShow(message, error: true)) return;
            ViewModel.ErrorMessage = message; ViewModel.IsError = true;
        });
    }
    private void OnBatchRemoveClick(object sender, RoutedEventArgs args)
    {
        if (_visit is not { } visit || _dialog is not null) return;
        var entries = MineList.SelectedItems.OfType<MineEntry>().ToArray();
        if (entries.Length == 0) return;
        var token = visit.Token;
        _ = Write(async () =>
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "取消标记", Content = $"确定取消这 {entries.Length} 部作品的标记吗？", PrimaryButtonText = "取消标记", CloseButtonText = "保留", DefaultButton = ContentDialogButton.Close };
            _dialog = dialog;
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && !token.IsCancellationRequested)
                    await ChangeEntriesAsync(entries, null);
            }
            finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
        });
    }
    private void OnTagChipTapped(object sender, TappedRoutedEventArgs args)
    {
        for (var source = args.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, sender); source = VisualTreeHelper.GetParent(source))
            if (source is ButtonBase) return;
        if (sender is FrameworkElement { Tag: string tag }) Read(() => ViewModel.SelectTagAsync(tag));
    }
    private void OnRemoveTagClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string tag }) _ = Write(() => ViewModel.RemoveTagAsync(tag));
    }
    private void OnOpenTagPageClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedTag is { } tag) _navigator.Navigate(typeof(TagSearchResultPage), tag);
    }
    private void OnMineItemClick(object sender, ItemClickEventArgs args)
    {
        if (!_batch && args.ClickedItem is MineEntry entry) _navigator.Navigate(typeof(AnimeDetailPage), entry.Anime.ID);
    }
    private void OnAnimeCardClicked(object? sender, AnimeCardClickedEventArgs args) => _navigator.Navigate(typeof(AnimeDetailPage), args.Anime.ID);
    private void OnManagedCoverLoaded(object sender, RoutedEventArgs args) => ConfigureCover(sender);
    private void OnManagedCoverDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => ConfigureCover(sender);
    private void OnManagedCoverUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image) ManagedImageLoader.Cancel(image);
    }
    private static void ConfigureCover(object sender)
    {
        if (sender is not Image { IsLoaded: true } image) return;
        if (image.Tag is Anime anime) ManagedImageLoader.ConfigureCover(image, anime.ID, anime.CoverURL, 44);
        else ManagedImageLoader.Cancel(image);
    }
}
