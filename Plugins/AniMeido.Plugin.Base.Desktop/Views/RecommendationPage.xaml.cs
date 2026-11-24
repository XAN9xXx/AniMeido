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
using System.ComponentModel;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class RecommendationPage : Page, INavigationAware, IDesktopManualRefresh
{
    private readonly IPluginNavigator _navigator;
    private readonly ILogger<RecommendationPage> _logger;
    private readonly DesktopPageOperations _operations = new();
    private readonly HashSet<AnimeCard> _candidates = [];
    private CancellationTokenSource? _visit;
    private ContentDialog? _dialog;
    private bool _restoring;
    private bool _restorePending;
    private bool _prefetching;
    private int _feedbackDepth;
    private Task _manualRefresh = Task.CompletedTask;

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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ShowSection(browse.Section);
    }

    public Task OnNavigatedToAsync(object? parameter) => _visit is null ? Task.CompletedTask : PendingActivation;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_visit is not null) return;
        _visit = new();
        _restorePending = true;
        ViewModel.PropertyChanged += OnViewModelChanged;
        PendingActivation = ActivateAsync(_visit.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_visit is not { } visit) return;
        SaveScroll();
        _visit = null;
        _dialog?.Hide();
        visit.Cancel();
        visit.Dispose();
        ViewModel.Suspend();
        ViewModel.PropertyChanged -= OnViewModelChanged;
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
        if (sender is not AnimeCard card) return;
        _candidates.Add(card);
        UpdateCandidate(card);
    }
    private void OnCandidateUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is AnimeCard card) _candidates.Remove(card);
    }
    private void OnCandidateDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        => UpdateCandidate((AnimeCard)sender);

    private void UpdateCandidate(AnimeCard card)
        => card.IsSelected = card.DataContext is RecommendationItem item && ViewModel.SelectedItem?.Anime.ID == item.Anime.ID;

    private void OnSectionButtonClick(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton { Tag: string section }) ShowSection(section);
    }
    private readonly DesktopSectionMotion _sectionMotion = new();

    internal void ShowSection(string section)
    {
        var previous = ViewModel.BrowseState.Section;
        SaveScroll();
        ViewModel.BrowseState.Section = section;
        RecommendationsPanel.Visibility = section == "recommendations" ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = section == "profile" ? Visibility.Visible : Visibility.Collapsed;
        HiddenPanel.Visibility = section == "hidden" ? Visibility.Visible : Visibility.Collapsed;
        RecommendationsSectionButton.IsChecked = section == "recommendations";
        ProfileSectionButton.IsChecked = section == "profile";
        HiddenSectionButton.IsChecked = section == "hidden";
        if (section == "recommendations") { _restorePending = true; RestoreScroll(); }
        if (section != previous) _sectionMotion.Play(SectionBody, section == "recommendations" || previous == "hidden");
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

    internal Task RefreshAsync(Action<string>? showSuccess = null)
    {
        if (!_manualRefresh.IsCompleted || ViewModel.IsBusy || ViewModel.IsRefreshing) return _manualRefresh;
        return _manualRefresh = RunActionAsync(async token =>
        {
            if (!await ViewModel.RefreshAsync(token, preferNewBatch: true) || token.IsCancellationRequested) return;
            ViewModel.BrowseState.VerticalOffset = 0;
            _restorePending = true;
            RestoreScroll();
        }, showManualRefresh: showSuccess);
    }

    public bool TryRefreshFromKeyboard(Action<string> showSuccess)
    {
        if (_visit is null || _dialog is not null) return false;
        _ = RefreshAsync(showSuccess);
        return true;
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
        return sender is FrameworkElement { Tag: RecommendationTagPreference tag }
            ? RunActionAsync(token => ViewModel.SaveTagAsync(tag, adjustment, token)) : Task.CompletedTask;
    }
    private static RecommendationAdjustment? Adjustment(int index) => index switch { 0 => RecommendationAdjustment.Like, 2 => RecommendationAdjustment.Reduce, _ => null };

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
        _dialog = dialog;
        dialog.XamlRoot = XamlRoot;
        try
        {
            var result = await dialog.ShowAsync();
            return token.IsCancellationRequested || !ReferenceEquals(visit, _visit) ? ContentDialogResult.None : result;
        }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }
    private Task RunActionAsync(Func<CancellationToken, Task> action, bool serialize = true, bool transient = true,
        Action<string>? showManualRefresh = null)
    {
        if (_visit is not { } visit) return Task.CompletedTask;
        var operation = ExecuteActionAsync(action, visit.Token, serialize, transient, showManualRefresh);
        _operations.Track(operation);
        return operation;
    }

    private async Task ExecuteActionAsync(Func<CancellationToken, Task> action, CancellationToken token, bool serialize, bool transient,
        Action<string>? showManualRefresh)
    {
        var previous = (ViewModel.Message, ViewModel.RefreshNotice, ViewModel.HasError);
        ++_feedbackDepth;
        try { await ViewModel.RunActionAsync(action, token, serialize); }
        finally
        {
            --_feedbackDepth;
            if (!token.IsCancellationRequested && (showManualRefresh is not null
                || previous != (ViewModel.Message, ViewModel.RefreshNotice, ViewModel.HasError)))
                UpdateStatus(transient, showManualRefresh);
        }
    }
    private void UpdateStatus(bool transient = false, Action<string>? showManualRefresh = null)
    {
        var message = string.Join(" ", new[] { ViewModel.Message, ViewModel.RefreshNotice }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var error = ViewModel.HasError || !string.IsNullOrWhiteSpace(ViewModel.RefreshNotice);
        if (!error && message.Length > 0 && showManualRefresh is not null)
        { showManualRefresh(message); StatusInfoBar.IsOpen = false; return; }
        if (transient && message.Length > 0 && DesktopFeedback.TryShow(message, error))
        { StatusInfoBar.IsOpen = false; return; }
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        StatusInfoBar.IsOpen = message.Length > 0;
    }
}
