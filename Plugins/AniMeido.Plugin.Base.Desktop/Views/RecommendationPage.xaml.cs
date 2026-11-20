using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
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
using Windows.System;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class RecommendationPage : Page, INavigationAware
{
    private readonly IPluginNavigator _navigator;
    private readonly ILogger<RecommendationPage> _logger;
    private readonly DesktopDeckOverlay _tags;
    private readonly DesktopPageOperations _operations = new();
    private readonly DispatcherTimer _tagOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _tagCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly HashSet<Border> _candidates = [];
    private TodayPlanStackPanel? _tagPanel;
    private CancellationTokenSource? _visit;
    private ContentDialog? _dialog;
    private bool _tagPinned;
    private bool _overTags;
    private bool _restoring;
    private bool _restorePending;
    private bool _prefetching;
    private int _feedbackDepth;

    public RecommendationViewModel ViewModel { get; }
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;
    internal Task PendingOperation { get => _operations.Pending; private set => _operations.Track(value); }
    internal Task PendingTags { get; private set; } = Task.CompletedTask;
    internal Task PendingPrefetch { get; private set; } = Task.CompletedTask;

    public RecommendationPage(RecommendationService recommendations, IPluginNavigator navigator,
        RecommendationBrowseState browse, ILogger<RecommendationPage> logger)
    {
        _navigator = navigator;
        _logger = logger;
        ViewModel = new(recommendations, browse, logger);
        InitializeComponent();
        _tags = new(TagPackHost, TagPackVisual, TagPackScroll, TagStackPopup, TagDismissCanvas, () => _tagPanel);
        _tagOpenTimer.Tick += (_, _) => { _tagOpenTimer.Stop(); if (_overTags) _tags.Open(); };
        _tagCloseTimer.Tick += (_, _) => { _tagCloseTimer.Stop(); if (!_overTags && !_tagPinned) _tags.Close(); };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnPageKeyDown;
        ShowSection(browse.Section);
    }

    public Task OnNavigatedToAsync(object? parameter) => _visit is null ? Task.CompletedTask : PendingActivation;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_visit is not null) return;
        _visit = new();
        _restorePending = true;
        ViewModel.PropertyChanged += OnViewModelChanged;
        DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged;
        PendingActivation = ActivateAsync(_visit.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_visit is not { } visit) return;
        SaveScroll();
        _visit = null;
        CloseTagStackImmediately();
        _dialog?.Hide();
        visit.Cancel();
        visit.Dispose();
        ViewModel.Suspend();
        ViewModel.PropertyChanged -= OnViewModelChanged;
        DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;
        ManagedImageLoader.Cancel(PreviewCover);
        _candidates.Clear();
        _restorePending = false;
        _restoring = false;
    }

    private async Task ActivateAsync(CancellationToken token)
    {
        try
        {
            await _operations.DrainAsync();
            if (token.IsCancellationRequested) return;
            await ViewModel.LoadAsync(token);
            if (token.IsCancellationRequested) return;
            UpdatePreview();
            UpdateMoreFooter();
            UpdateOnboardingControls();
            UpdateStatus();
            RestoreScroll();
            PendingTags = LoadTagsAsync(token);
            await PendingTags;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 最后加载边界保留页面并提供重试。
        catch (Exception error)
        {
            _logger.LogError(error, "推荐页读取失败。");
            if (!token.IsCancellationRequested) ViewModel.ReportError("推荐暂时无法加载，请按 F5 重试。");
        }
#pragma warning restore CA1031
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(RecommendationViewModel.SelectedItem):
                CloseTagStackImmediately();
                UpdatePreview();
                if (_visit is { } visit) PendingTags = LoadTagsAsync(visit.Token);
                break;
            case nameof(RecommendationViewModel.HasItems): UpdatePreview(); break;
            case nameof(RecommendationViewModel.IsLoadingTags):
            case nameof(RecommendationViewModel.TagError): UpdateTagLoadingState(); break;
            case nameof(RecommendationViewModel.SavingFollowingIds): UpdateFollow(); break;
            case nameof(RecommendationViewModel.IsLoadingMore):
            case nameof(RecommendationViewModel.HasMoreRecommendations):
            case nameof(RecommendationViewModel.LoadMoreError): UpdateMoreFooter(); break;
            case nameof(RecommendationViewModel.IsRefreshing): RefreshButton.IsEnabled = !ViewModel.IsRefreshing; break;
            case nameof(RecommendationViewModel.Message):
            case nameof(RecommendationViewModel.HasError):
            case nameof(RecommendationViewModel.RefreshNotice):
                if (_feedbackDepth == 0) UpdateStatus();
                break;
        }
    }

    private void UpdatePreview()
    {
        var item = ViewModel.SelectedItem;
        SelectedPreviewContent.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        NoSelectionText.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanel.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyRecommendations.Visibility = ViewModel.HasItems || ViewModel.HasMoreRecommendations ? Visibility.Collapsed : Visibility.Visible;
        if (item is not null && IsLoaded) ManagedImageLoader.ConfigureCover(PreviewCover, item.Anime.ID, item.Anime.CoverURL, 210);
        else ManagedImageLoader.Cancel(PreviewCover);
        foreach (var candidate in _candidates) UpdateCandidate(candidate);
        UpdateDesktopRecommendationNavigation();
        UpdateFollow();
    }

    private void UpdateFollow() => SelectedFollowButton.IsEnabled = ViewModel.SelectedItem is { } selected
        && !ViewModel.SavingFollowingIds.Contains(selected.Anime.ID);

    private async Task LoadTagsAsync(CancellationToken token)
    {
        try { await ViewModel.LoadSelectedTagsAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 预览错误在页面内显示。
        catch (Exception error)
        {
            _logger.LogError(error, "推荐标签读取失败。");
            if (!token.IsCancellationRequested) ViewModel.ReportError("标签暂时无法加载，请重试。");
        }
#pragma warning restore CA1031
    }

    private void OnCandidateClick(object? sender, AnimeCardClickedEventArgs args)
        => ViewModel.SelectedItem = ViewModel.Items.FirstOrDefault(item => item.Anime.ID == args.Anime.ID);

    private void OnCandidateLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Border border) return;
        _candidates.Add(border);
        UpdateCandidate(border);
    }
    private void OnCandidateUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Border border) _candidates.Remove(border);
    }
    private void UpdateCandidate(Border border)
        => border.Background = border.Tag is RecommendationItem item && ViewModel.SelectedItem?.Anime.ID == item.Anime.ID
            ? (Brush)Application.Current.Resources["DesktopSelectedBrush"] : null;

    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        CloseTagStackImmediately();
        foreach (var candidate in _candidates) UpdateCandidate(candidate);
    }

    private void OnSectionButtonClick(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton { Tag: string section }) ShowSection(section);
    }
    internal void ShowSection(string section)
    {
        SaveScroll();
        CloseTagStackImmediately();
        ViewModel.BrowseState.Section = section;
        RecommendationsPanel.Visibility = section == "recommendations" ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = section == "profile" ? Visibility.Visible : Visibility.Collapsed;
        HiddenPanel.Visibility = section == "hidden" ? Visibility.Visible : Visibility.Collapsed;
        RecommendationsSectionButton.IsChecked = section == "recommendations";
        ProfileSectionButton.IsChecked = section == "profile";
        HiddenSectionButton.IsChecked = section == "hidden";
        if (section == "recommendations") { _restorePending = true; RestoreScroll(); }
    }

    private void SaveScroll()
    {
        if (_visit is null || _restorePending || _restoring || RecommendationsPanel.Visibility != Visibility.Visible) return;
        ViewModel.BrowseState.VerticalOffset = CandidateScroll.HorizontalOffset;
    }
    private void RestoreScroll()
    {
        if (_visit is not { } visit || !_restorePending || ViewModel.IsBusy || RecommendationsPanel.Visibility != Visibility.Visible) return;
        _restoring = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(visit, _visit)) return;
            CandidateScroll.UpdateLayout();
            CandidateScroll.ChangeView(ViewModel.BrowseState.VerticalOffset, null, null, true);
            _restorePending = false;
            _restoring = false;
            PendingPrefetch = PrefetchAsync();
        });
    }
    private void OnCandidateScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        SaveScroll();
        PendingPrefetch = PrefetchAsync();
    }
    private void OnFeedScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args) => CloseTagStackImmediately();

    internal async Task PrefetchAsync()
    {
        if (_visit is not { } visit || _prefetching || _restorePending || _restoring
            || RecommendationsPanel.Visibility != Visibility.Visible || ViewModel.IsBusy || ViewModel.IsRefreshing
            || ViewModel.IsLoadingMore || !ViewModel.HasMoreRecommendations || !string.IsNullOrWhiteSpace(ViewModel.LoadMoreError)
            || CandidateScroll.ScrollableWidth - CandidateScroll.HorizontalOffset > 800) return;
        _prefetching = true;
        var token = visit.Token;
        try
        {
            int previous;
            do
            {
                previous = ViewModel.Items.Count;
                await ViewModel.LoadMoreAsync(token);
                if (token.IsCancellationRequested) return;
                CandidateScroll.UpdateLayout();
            } while (ViewModel.Items.Count > previous && ViewModel.HasMoreRecommendations
                && string.IsNullOrWhiteSpace(ViewModel.LoadMoreError) && RecommendationsPanel.Visibility == Visibility.Visible
                && CandidateScroll.ScrollableWidth - CandidateScroll.HorizontalOffset <= 800);
            UpdateMoreFooter();
        }
        finally
        {
            _prefetching = false;
            if (_visit is not null && !ReferenceEquals(visit, _visit))
                DispatcherQueue.TryEnqueue(() => PendingPrefetch = PrefetchAsync());
        }
    }

    private void OnLoadMoreClick(object sender, RoutedEventArgs args)
    {
        if (_visit is { } visit) PendingOperation = ViewModel.LoadMoreAsync(visit.Token);
    }
    private void UpdateMoreFooter()
    {
        MoreStatusText.Text = ViewModel.IsLoadingMore ? "正在加载更多作品…" : ViewModel.LoadMoreError
            ?? (ViewModel.HasMoreRecommendations ? "" : ViewModel.HasItems ? "已显示本轮全部作品" : "");
        LoadMoreButton.Content = string.IsNullOrWhiteSpace(ViewModel.LoadMoreError) ? "加载更多" : "重试加载";
        LoadMoreButton.Visibility = !ViewModel.IsLoadingMore && (ViewModel.HasMoreRecommendations || !string.IsNullOrWhiteSpace(ViewModel.LoadMoreError))
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateDesktopRecommendationNavigation();
    }

    internal Task RefreshAsync() => RunActionAsync(async token =>
    {
        if (ViewModel.IsBusy || ViewModel.IsRefreshing) return;
        if (!await ViewModel.RefreshAsync(token, preferNewBatch: true) || token.IsCancellationRequested) return;
        ViewModel.BrowseState.VerticalOffset = 0;
        _restorePending = true;
        RestoreScroll();
    }, transient: false);
    private void OnRefreshClick(object sender, RoutedEventArgs args) => PendingOperation = RefreshAsync();
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.F5) return;
        args.Handled = true;
        PendingOperation = RefreshAsync();
    }
    private void OnSelectedDetailsClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedItem is not { } selected) return;
        SaveScroll();
        _navigator.Navigate(typeof(AnimeDetailPage), selected.Anime.ID);
    }
    private void OnSelectedFollowClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedItem is { } item) _ = RunActionAsync(token => ViewModel.ToggleFollowingAsync(item, token), serialize: false);
    }
    private void OnSelectedSkipClick(object sender, RoutedEventArgs args)
    {
        if (_visit is null || ViewModel.SelectedItem is not { } item) return;
        ViewModel.Skip(item);
        UpdateStatus(transient: true);
    }
    private void OnSelectedHideClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedItem is { } item) _ = RunActionAsync(token => ViewModel.HideAsync(item, token));
    }
    private void OnSelectedNotInterestedClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedItem is { } item) _ = RunActionAsync(async token =>
        {
            var dialog = Confirmation("标记为不感兴趣", $"《{item.Anime.Title}》之后会移出推荐。可以在“我的番剧”里撤销。", "确认");
            if (await ShowDialogAsync(dialog, token) == ContentDialogResult.Primary) await ViewModel.MarkNotInterestedAsync(item, token);
        });
    }

    private void OnPreferenceClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: RecommendationFeature feature }) _ = RunActionAsync(async token =>
        {
            var adjustment = ViewModel.Profile.FirstOrDefault(item => item.Feature.Kind == feature.Kind && item.Feature.Key == feature.Key)?.Adjustment;
            var choices = new DesktopChoice { SelectedIndex = DesktopRecommendationPreference.SelectionIndex(adjustment == RecommendationAdjustment.Like, adjustment == RecommendationAdjustment.Reduce) };
            choices.Items.Add("喜欢"); choices.Items.Add("未设置"); choices.Items.Add("减少");
            if (await ShowDialogAsync(Confirmation(feature.DisplayName, choices, "应用"), token) == ContentDialogResult.Primary)
                await ViewModel.SetPreferenceAsync(feature, Adjustment(choices.SelectedIndex), token);
        });
    }
    private void OnRestoreClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: RecommendationHiddenAnime item }) _ = RunActionAsync(token => ViewModel.RestoreAsync(item, token));
    }
    private void OnClearHiddenClick(object sender, RoutedEventArgs args)
        => _ = RunActionAsync(async token =>
        {
            if (ViewModel.HiddenAnime.Count > 0 && await ShowDialogAsync(Confirmation("恢复全部隐藏作品", "刷新后，这些作品可能再次出现。", "全部恢复"), token) == ContentDialogResult.Primary)
                await ViewModel.ClearHiddenAsync(token);
        });
    private void OnClearPreferencesClick(object sender, RoutedEventArgs args)
        => _ = RunActionAsync(async token =>
        {
            if (await ShowDialogAsync(Confirmation("清除手动设置", "之后按你的记录自动判断，推荐会重新生成。", "清除并刷新"), token) == ContentDialogResult.Primary)
                await ViewModel.ClearPreferencesAsync(token);
        }, transient: false);

    private void OnOnboardingTagToggle(object sender, RoutedEventArgs args) => UpdateOnboardingControls();
    private void OnRefreshSuggestedTagsClick(object sender, RoutedEventArgs args) { ViewModel.RefreshSuggestedTags(); UpdateOnboardingControls(); }
    private void UpdateOnboardingControls()
    {
        var count = ViewModel.SelectedOnboardingTags.Count;
        GenerateFromTagsButton.IsEnabled = count > 0;
        GenerateFromTagsButton.Content = count == 0 ? "使用所选标签生成推荐" : $"使用所选 {count} 个标签生成推荐";
    }
    private void OnGenerateFromTagsClick(object sender, RoutedEventArgs args)
    {
        var tags = ViewModel.SelectedOnboardingTags;
        if (tags.Count > 0) _ = RunActionAsync(token => ViewModel.ApplyOnboardingTagsAsync(tags, token), transient: false);
    }

    private void UpdateTagLoadingState()
    {
        TagLoadingRing.Visibility = ViewModel.IsLoadingTags ? Visibility.Visible : Visibility.Collapsed;
        var error = !string.IsNullOrWhiteSpace(ViewModel.TagError);
        TagErrorText.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        RetryTagsButton.Visibility = error && !ViewModel.IsLoadingTags ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnRetryTagsClick(object sender, RoutedEventArgs args)
    {
        if (_visit is { } visit) PendingTags = LoadTagsAsync(visit.Token);
    }
    private Task SaveTagAsync(object sender, RecommendationAdjustment? adjustment)
    {
        _tagPinned = _tags.IsOpen;
        return sender is FrameworkElement { Tag: RecommendationTagPreference tag }
            ? RunActionAsync(token => ViewModel.SaveTagAsync(tag, adjustment, token)) : Task.CompletedTask;
    }
    private static RecommendationAdjustment? Adjustment(int index) => index switch { 0 => RecommendationAdjustment.Like, 2 => RecommendationAdjustment.Reduce, _ => null };

    private void OnTagPanelLoaded(object sender, RoutedEventArgs args) => _tagPanel = (TodayPlanStackPanel)sender;
    private void OnTagPackEntered(object sender, PointerRoutedEventArgs args)
    {
        _overTags = true;
        _tagCloseTimer.Stop();
        if (_visit is not null && !_tags.IsOpen && ViewModel.SelectedTags.Count > 1) _tagOpenTimer.Start();
    }
    private void OnTagPackExited(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(TagPackVisual).Position;
        if (point.X >= 0 && point.X < TagPackVisual.ActualWidth && point.Y >= 0 && point.Y < TagPackVisual.ActualHeight) return;
        _overTags = false;
        _tagOpenTimer.Stop();
        if (!_tagPinned) _tagCloseTimer.Start();
    }
    private void OnTagPackTapped(object sender, TappedRoutedEventArgs args)
    {
        // 编辑器负责自己的点击；点击卡面空白才展开。
        for (var node = args.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, sender); node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase or DesktopChoice) return;
        if (_tags.IsOpen) CloseTagStackImmediately();
        else if (ViewModel.SelectedTags.Count > 1) { _tagPinned = true; _tags.Open(); }
        args.Handled = true;
    }
    private void OnTagStackKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Enter or VirtualKey.Space) || !ReferenceEquals(args.OriginalSource, _tagPanel)) return;
        args.Handled = true;
        _tagPinned = true;
        _tags.Open();
    }
    private void CloseTagStackImmediately()
    {
        _tagOpenTimer.Stop(); _tagCloseTimer.Stop();
        _tagPinned = false; _overTags = false;
        _tags?.Close();
    }
    private void OnCoverUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image) ManagedImageLoader.Cancel(image);
    }

    private static ContentDialog Confirmation(string title, object content, string primary) => new()
    {
        Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
    };
    internal async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, CancellationToken token)
    {
        if (token.IsCancellationRequested || _visit is not { } visit || XamlRoot is null || _dialog is not null) return ContentDialogResult.None;
        CloseTagStackImmediately();
        _dialog = dialog;
        dialog.XamlRoot = XamlRoot;
        try
        {
            var result = await dialog.ShowAsync();
            return token.IsCancellationRequested || !ReferenceEquals(visit, _visit) ? ContentDialogResult.None : result;
        }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }
    private Task RunActionAsync(Func<CancellationToken, Task> action, bool serialize = true, bool transient = true)
    {
        if (_visit is not { } visit) return Task.CompletedTask;
        var operation = ExecuteActionAsync(action, visit.Token, serialize, transient);
        _operations.Track(operation);
        return operation;
    }

    private async Task ExecuteActionAsync(Func<CancellationToken, Task> action, CancellationToken token, bool serialize, bool transient)
    {
        var message = ViewModel.Message;
        ++_feedbackDepth;
        try { await ViewModel.RunActionAsync(action, token, serialize); }
        finally
        {
            --_feedbackDepth;
            if (!token.IsCancellationRequested && message != ViewModel.Message) UpdateStatus(transient);
        }
    }
    private void UpdateStatus(bool transient = false)
    {
        if (transient && !string.IsNullOrWhiteSpace(ViewModel.Message) && DesktopFeedback.TryShow(ViewModel.Message, ViewModel.HasError))
        { StatusInfoBar.IsOpen = false; return; }
        StatusInfoBar.Message = string.Join(" ", new[] { ViewModel.Message, ViewModel.RefreshNotice }.Where(message => !string.IsNullOrWhiteSpace(message)));
        StatusInfoBar.Severity = ViewModel.HasError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        StatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(StatusInfoBar.Message);
    }
}
