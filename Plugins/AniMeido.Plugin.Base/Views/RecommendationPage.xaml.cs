using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class RecommendationPage : Page, INavigationAware
{
    private readonly IPluginNavigator _navigator;
    private CancellationTokenSource? _navigationCancellation;
    private bool _isActionRunning;
    private ScrollViewer? _listScroll;
    private bool _restoringScroll;
    private bool _scrollRestorePending;
    private int _scrollRestoreGeneration;
    private bool _isNarrow;
    private bool _previewOpen;
    private bool _tagPinned;
    private bool _overTagEntry;
    private readonly DispatcherTimer _tagOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _tagCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };


    public RecommendationPage(
        RecommendationService recommendations,
        IPluginNavigator navigator,
        RecommendationBrowseState browse)
    {
        _navigator = navigator;
        ViewModel = new RecommendationViewModel(recommendations, browse);
        InitializeComponent();
        Unloaded += OnUnloaded;
        Loaded += OnLoaded;
        _tagOpenTimer.Tick += OnTagOpenTick;
        _tagCloseTimer.Tick += OnTagCloseTick;
        ShowSection(browse.Section);
    }

    public RecommendationViewModel ViewModel { get; }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = new CancellationTokenSource();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _scrollRestorePending = true;
        var token = _navigationCancellation.Token;
        await ViewModel.LoadAsync(token);
        if (token.IsCancellationRequested) return;
        UpdatePreview();
        RestoreScroll();
        UpdateMoreFooter();
        TryLoadMoreNearEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SaveScroll();
        _navigationCancellation?.Cancel();
        ViewModel.Suspend();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _tagOpenTimer.Stop();
        _tagCloseTimer.Stop();
        CloseTagStackImmediately();
        if (_listScroll is not null) _listScroll.ViewChanged -= OnListViewChanged;
        _listScroll = null;
        _scrollRestoreGeneration++;
        _restoringScroll = false;
        _scrollRestorePending = false;
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = null;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var applied = false;
        await RunActionAsync(async () =>
        {
            applied = await ViewModel.RefreshAsync(CurrentToken, preferNewBatch: true);
        });
        if (applied && _navigationCancellation is not null)
        {
            ViewModel.BrowseState.VerticalOffset = 0;
            RestoreScroll();
            TryLoadMoreNearEnd();
        }
    }

    private void OnSectionButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string section })
        {
            ShowSection(section);
        }
    }

    private void ShowSection(string section)
    {
        ViewModel.BrowseState.Section = section;
        CloseTagStackImmediately();
        RecommendationsPanel.Visibility = section == "recommendations"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProfilePanel.Visibility = section == "profile"
            ? Visibility.Visible
            : Visibility.Collapsed;
        HiddenPanel.Visibility = section == "hidden"
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecommendationsSectionButton.IsChecked = section == "recommendations";
        ProfileSectionButton.IsChecked = section == "profile";
        HiddenSectionButton.IsChecked = section == "hidden";
        if (section == "recommendations" && _scrollRestorePending) RestoreScroll();
        if (section == "recommendations") TryLoadMoreNearEnd();
    }

    private async Task ConfirmNotInterestedAsync(RecommendationItem item)
    {
        var token = CurrentToken;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "标记为不感兴趣",
            Content = $"“{item.Anime.Title}”会写入追番状态，并从推荐中排除。",
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !token.IsCancellationRequested)
            await ViewModel.MarkNotInterestedAsync(item, token);
    }

    private async void OnPreferenceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationFeature feature })
        {
            await RunActionAsync(() => ShowPreferenceDialogAsync(feature));
        }
    }

    private async Task ShowPreferenceDialogAsync(
        RecommendationFeature feature)
    {
        var choices = new ComboBox
        {
            Header = $"此{feature.KindText}的推荐倾向",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = ViewModel.Profile.FirstOrDefault(item => item.Feature.Kind == feature.Kind
                && item.Feature.Key == feature.Key)?.Adjustment switch
            {
                RecommendationAdjustment.Like => 0,
                RecommendationAdjustment.Reduce => 2,
                _ => 1,
            },
        };
        choices.Items.Add("喜欢");
        choices.Items.Add("未设置（恢复自动判断）");
        choices.Items.Add("减少");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = feature.DisplayName,
            Content = choices,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var adjustment = choices.SelectedIndex switch
        {
            0 => RecommendationAdjustment.Like,
            2 => RecommendationAdjustment.Reduce,
            _ => (RecommendationAdjustment?)null,
        };
        await ViewModel.SetPreferenceAsync(
            feature,
            adjustment,
            CurrentToken);
    }

    private void OnOnboardingTagToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle)
        {
            return;
        }

        if (toggle.DataContext is not RecommendationTagOption option)
        {
            return;
        }

        option.IsSelected = toggle.IsChecked == true;
        UpdateOnboardingControls();
    }

    private void OnRefreshSuggestedTagsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshSuggestedTags();
        UpdateOnboardingControls();
    }

    private void UpdateOnboardingControls()
    {
        var selectedCount = ViewModel.SelectedOnboardingTags.Count;
        GenerateFromTagsButton.IsEnabled = selectedCount > 0;
        GenerateFromTagsButton.Content = selectedCount > 0
            ? $"使用所选 {selectedCount} 个标签生成推荐"
            : "使用所选标签生成推荐";
    }

    private async void OnGenerateFromTagsClick(
        object sender,
        RoutedEventArgs e)
    {
        var selected = ViewModel.SelectedOnboardingTags;
        if (selected.Count == 0)
        {
            return;
        }

        GenerateFromTagsButton.IsEnabled = false;
        await RunActionAsync(() =>
            ViewModel.ApplyOnboardingTagsAsync(selected, CurrentToken));
        GenerateFromTagsButton.IsEnabled = ViewModel.IsColdStart
            && ViewModel.SelectedOnboardingTags.Count > 0;
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationHiddenAnime item })
        {
            await RunActionAsync(() => ViewModel.RestoreAsync(item, CurrentToken));
        }
    }

    private async void OnClearHiddenClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HiddenAnime.Count == 0)
        {
            return;
        }

        var dialog = CreateConfirmationDialog(
            "恢复全部隐藏作品",
            "这些作品会在下次刷新时重新参与推荐。",
            "全部恢复");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunActionAsync(() => ViewModel.ClearHiddenAsync(CurrentToken));
        }
    }

    private async void OnClearPreferencesClick(object sender, RoutedEventArgs e)
    {
        var dialog = CreateConfirmationDialog(
            "清除全部手工偏好",
            "系统推断仍会保留，推荐将按本地记录重新计算。",
            "清除并刷新");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunActionAsync(() =>
                ViewModel.ClearPreferencesAsync(CurrentToken));
        }
    }

    private ContentDialog CreateConfirmationDialog(
        string title,
        string content,
        string primaryText)
        => new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

    private void OnViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecommendationViewModel.IsLoadingTags)
            or nameof(RecommendationViewModel.TagError)) UpdateTagLoadingState();
        if (e.PropertyName == nameof(RecommendationViewModel.TagSummary)
            && TagStackPopup?.IsOpen == true)
            DispatcherQueue.TryEnqueue(UpdateTagStackSize);
        if (e.PropertyName is nameof(RecommendationViewModel.SelectedItem)
            or nameof(RecommendationViewModel.HasItems)) UpdatePreview();
        if (e.PropertyName is nameof(RecommendationViewModel.IsLoadingMore)
            or nameof(RecommendationViewModel.HasMoreRecommendations)
            or nameof(RecommendationViewModel.LoadMoreError)) UpdateMoreFooter();
        if (e.PropertyName == nameof(RecommendationViewModel.SavingFollowingIds))
        {
            RefreshFollowButtons(RecommendationList);
            UpdateSelectedFollowButton();
        }
        if (e.PropertyName == nameof(RecommendationViewModel.FollowLabel)
            && ViewModel.SelectedItem is { } selected
            && RecommendationList.ContainerFromItem(selected) is DependencyObject container)
            RefreshFollowButtons(container);
        if (e.PropertyName is nameof(RecommendationViewModel.Message)
            or nameof(RecommendationViewModel.RefreshNotice)
            or nameof(RecommendationViewModel.HasError))
        {
            StatusInfoBar.Message = string.Join(" ", new[]
            {
                ViewModel.Message,
                ViewModel.RefreshNotice,
            }.Where(message => !string.IsNullOrWhiteSpace(message)));
            StatusInfoBar.Severity = ViewModel.HasError
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Informational;
            StatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(
                StatusInfoBar.Message);
        }
        else if (e.PropertyName
            == nameof(RecommendationViewModel.IsRefreshing))
        {
            RefreshButton.IsEnabled = !ViewModel.IsRefreshing;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        // 首次进入由 OnNavigatedToAsync 开始；返回时恢复的是同一个页面实例，
        // 不会再调用它，离开时取消的内容在这里接上。
        if (_navigationCancellation is null)
        {
            _ = ResumeAsync();
            return;
        }

        UpdatePreview();
        RestoreScroll();
    }

    private async Task ResumeAsync()
    {
        _navigationCancellation = new CancellationTokenSource();
        var token = _navigationCancellation.Token;
        _scrollRestorePending = true;
        RefreshButton.IsEnabled = !ViewModel.IsRefreshing;
        UpdateTagLoadingState();
        try
        {
            if (ViewModel.TakeResumeReload())
            {
                await ViewModel.LoadAsync(token);
                if (token.IsCancellationRequested) return;
            }

            UpdatePreview();
            RestoreScroll();
            UpdateMoreFooter();
            TryLoadMoreNearEnd();
            // 在详情页里可能改了关注或其他标记：重新读取当前作品的标签与状态。
            await LoadTagsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 返回页面时的恢复失败只提示，不中断页面。
        catch (Exception ex) { if (!token.IsCancellationRequested) ViewModel.ReportError(ex.Message); }
#pragma warning restore CA1031
    }

    private void UpdatePreview()
    {
        if (PreviewPanel is null) return;
        var item = ViewModel.SelectedItem;
        SelectedPreviewContent.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        NoSelectionText.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyRecommendations.Visibility = ViewModel.HasItems
            || ViewModel.HasMoreRecommendations ? Visibility.Collapsed : Visibility.Visible;
        if (item is null) ManagedImageLoader.Cancel(PreviewCover);
        else ManagedImageLoader.ConfigureCover(PreviewCover, item.Anime.ID, item.Anime.CoverURL, 110);
        ApplyPreviewLayout();
        UpdateSelectedFollowButton();
    }

    private async void OnRecommendationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CloseTagStackImmediately();
        ViewModel.SelectedItem = RecommendationList.SelectedItem as RecommendationItem;
        UpdatePreview();
        if (_navigationCancellation is not null)
            await LoadTagsAsync();
    }

    private async Task LoadTagsAsync()
    {
        var token = CurrentToken;
        try { await ViewModel.LoadSelectedTagsAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // Preview failures are contained at the asynchronous UI boundary.
        catch (Exception ex) { if (!token.IsCancellationRequested) ViewModel.ReportError(ex.Message); }
#pragma warning restore CA1031
    }

    private void OnRecommendationItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecommendationItem item) ViewModel.SelectedItem = item;
        _previewOpen = true;
        ApplyPreviewLayout();
    }

    private void OnBrowseSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _isNarrow = e.NewSize.Width < 900;
        ApplyPreviewLayout();
    }

    private void ApplyPreviewLayout()
    {
        if (PreviewColumn is null) return;
        PreviewColumn.Width = _isNarrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        Grid.SetColumn(PreviewPanel, _isNarrow ? 0 : 1);
        Grid.SetColumnSpan(PreviewPanel, _isNarrow ? 2 : 1);
        PreviewPanel.HorizontalAlignment = _isNarrow ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        PreviewPanel.Width = _isNarrow ? Math.Min(440, Math.Max(0, BrowseGrid.ActualWidth)) : double.NaN;
        PreviewPanel.Visibility = !_isNarrow || (_previewOpen && ViewModel.HasSelection) ? Visibility.Visible : Visibility.Collapsed;
        ClosePreviewButton.Visibility = _isNarrow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClosePreviewClick(object sender, RoutedEventArgs e)
    {
        CloseTagStackImmediately();
        _previewOpen = false;
        ApplyPreviewLayout();
        RecommendationList.Focus(FocusState.Programmatic);
    }

    private void OnSelectedDetailsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item) return;
        SaveScroll();
        _navigator.Navigate(typeof(AnimeDetailPage), item.Anime.ID);
    }

    private async void OnFollowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecommendationItem item } button) return;

        await RunActionAsync(() => ViewModel.ToggleFollowingAsync(item, CurrentToken), serialize: false);
        if (button.Tag is RecommendationItem current) UpdateFollowButton(button, current);
    }

    private void OnFollowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationItem item } button)
            UpdateFollowButton(button, item);
    }

    private void OnFollowDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Button button && args.NewValue is RecommendationItem item)
            UpdateFollowButton(button, item);
    }

    private async void OnSelectedFollowClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item) return;
        await RunActionAsync(() => ViewModel.ToggleFollowingAsync(item, CurrentToken), serialize: false);
        if (RecommendationList.ContainerFromItem(item) is DependencyObject container)
            RefreshFollowButtons(container);
    }

    private void RefreshFollowButtons(DependencyObject root)
    {
        if (root is Button { Name: "FollowButton", Tag: RecommendationItem item } button)
            UpdateFollowButton(button, item);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            RefreshFollowButtons(VisualTreeHelper.GetChild(root, i));
    }

    private void UpdateFollowButton(Button button, RecommendationItem item)
    {
        button.Content = ViewModel.BrowseState.TrackingLabels.GetValueOrDefault(item.Anime.ID, "+ 关注");
        button.IsEnabled = !ViewModel.SavingFollowingIds.Contains(item.Anime.ID);
    }

    private void UpdateSelectedFollowButton()
    {
        SelectedFollowButton.IsEnabled = ViewModel.SelectedItem is { } item
            && !ViewModel.SavingFollowingIds.Contains(item.Anime.ID);
    }

    private void OnSelectedSkipClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item) ViewModel.Skip(item);
    }

    private void OnRowSkipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationItem item }) ViewModel.Skip(item);
    }

    private async void OnRowHideClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationItem item })
            await RunActionAsync(() => ViewModel.HideAsync(item, CurrentToken));
    }

    private async void OnRowNotInterestedClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationItem item })
            await RunActionAsync(() => ConfirmNotInterestedAsync(item));
    }

    private async void OnSelectedHideClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await RunActionAsync(() => ViewModel.HideAsync(item, CurrentToken));
    }

    private async void OnSelectedNotInterestedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await RunActionAsync(() => ConfirmNotInterestedAsync(item));
    }

    private void OnCoverLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) ConfigureRowCover(image);
    }

    private void OnCoverDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Image image && image.IsLoaded) ConfigureRowCover(image);
    }

    private static void ConfigureRowCover(Image image)
    {
        var anime = (image.DataContext as RecommendationItem)?.Anime ?? image.Tag as Anime;
        if (anime is not null) ManagedImageLoader.ConfigureCover(image, anime.ID, anime.CoverURL, 72);
        else ManagedImageLoader.Cancel(image);
    }

    private void OnCoverUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) ManagedImageLoader.Cancel(image);
    }

    private void OnRecommendationListLoaded(object sender, RoutedEventArgs e)
    {
        if (_listScroll is not null) _listScroll.ViewChanged -= OnListViewChanged;
        _listScroll = FindScrollViewer(RecommendationList);
        if (_listScroll is not null) _listScroll.ViewChanged += OnListViewChanged;
        RestoreScroll();
        UpdateMoreFooter();
        TryLoadMoreNearEnd();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void SaveScroll()
    {
        if (_listScroll is not null && !_restoringScroll && !_scrollRestorePending)
            ViewModel.BrowseState.VerticalOffset = _listScroll.VerticalOffset;
    }

    private void OnListViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        SaveScroll();
        if (!e.IsIntermediate) TryLoadMoreNearEnd();
    }

    private async void TryLoadMoreNearEnd()
    {
        if (_listScroll is null || _navigationCancellation is null
            || RecommendationsPanel.Visibility != Visibility.Visible
            || ViewModel.IsLoadingMore || !ViewModel.HasMoreRecommendations
            || !string.IsNullOrWhiteSpace(ViewModel.LoadMoreError)
            || ViewModel.IsBusy || ViewModel.IsRefreshing
            || _listScroll.ScrollableHeight - _listScroll.VerticalOffset > 400) return;
        await ViewModel.LoadMoreAsync(CurrentToken);
        UpdateMoreFooter();
    }

    private async void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadMoreAsync(CurrentToken);
        UpdateMoreFooter();
    }

    private void UpdateMoreFooter()
    {
        if (MoreStatusText is null || LoadMoreButton is null) return;
        MoreStatusText.Text = ViewModel.IsLoadingMore ? "正在加载更多作品…"
            : ViewModel.LoadMoreError ?? (ViewModel.HasMoreRecommendations
                ? ViewModel.HasItems
                    ? "继续向下滚动或点击加载更多"
                    : "点击加载更多继续查找"
                : "已显示本轮全部作品");
        LoadMoreButton.Content = string.IsNullOrWhiteSpace(ViewModel.LoadMoreError)
            ? "加载更多" : "重试加载";
        LoadMoreButton.Visibility = !ViewModel.IsLoadingMore
            && (ViewModel.HasMoreRecommendations
                || !string.IsNullOrWhiteSpace(ViewModel.LoadMoreError))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreScroll()
    {
        var scroll = _listScroll;
        var navigation = _navigationCancellation;
        if (scroll is null || navigation is null || ViewModel.IsBusy
            || RecommendationsPanel.Visibility != Visibility.Visible) return;
        var generation = ++_scrollRestoreGeneration;
        _restoringScroll = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (generation != _scrollRestoreGeneration || navigation != _navigationCancellation
                    || scroll != _listScroll) return;
                if (RecommendationsPanel.Visibility != Visibility.Visible)
                {
                    _scrollRestorePending = true;
                    return;
                }
                RecommendationList.UpdateLayout();
                scroll.ChangeView(null, ViewModel.BrowseState.VerticalOffset, null, true);
                _scrollRestorePending = false;
            }
            finally
            {
                if (generation == _scrollRestoreGeneration) _restoringScroll = false;
            }
        }))
        {
            _restoringScroll = false;
            _scrollRestorePending = false;
        }
    }

    private void UpdateTagLoadingState()
    {
        TagLoadingRing.Visibility = ViewModel.IsLoadingTags ? Visibility.Visible : Visibility.Collapsed;
        var hasError = !string.IsNullOrWhiteSpace(ViewModel.TagError);
        TagErrorText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        RetryTagsButton.Visibility = hasError && !ViewModel.IsLoadingTags ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRetryTagsClick(object sender, RoutedEventArgs e) => await LoadTagsAsync();
    private async void OnTagLikeClick(object sender, RoutedEventArgs e) => await SaveTagAsync(sender, RecommendationAdjustment.Like);
    private async void OnTagResetClick(object sender, RoutedEventArgs e) => await SaveTagAsync(sender, null);
    private async void OnTagReduceClick(object sender, RoutedEventArgs e) => await SaveTagAsync(sender, RecommendationAdjustment.Reduce);

    private async Task SaveTagAsync(object sender, RecommendationAdjustment? adjustment)
    {
        _tagPinned = TagStackPopup.IsOpen;
        if (sender is ToggleButton { Tag: RecommendationTagPreference tag })
        {
            // A click must not claim a saved preference before persistence succeeds.
            tag.RefreshSelection();
            try { await RunActionAsync(() => ViewModel.SaveTagAsync(tag, adjustment, CurrentToken)); }
            finally { tag.RefreshSelection(); }
        }
    }

    private CancellationToken CurrentToken
        => _navigationCancellation?.Token ?? CancellationToken.None;

    private async Task RunActionAsync(Func<Task> action, bool serialize = true)
    {
        if (serialize && _isActionRunning)
        {
            ViewModel.ReportBusyAction();
            return;
        }

        if (serialize) _isActionRunning = true;
        var cancellationToken = CurrentToken;
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // UI 边界将可恢复错误转为页面提示，避免 async void 终止进程。
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested) ViewModel.ReportError(ex.Message);
        }
#pragma warning restore CA1031
        finally
        {
            if (serialize) _isActionRunning = false;
        }
    }
}
