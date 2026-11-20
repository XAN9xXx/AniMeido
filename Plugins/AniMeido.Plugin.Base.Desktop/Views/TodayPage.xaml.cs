using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;
using Windows.System;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class TodayPage : Page, INavigationAware
{
    private readonly ActionCenterService _actionCenter;
    private readonly ArchiveService _archive;
    private readonly PlanReminderCoordinator _reminders;
    private readonly IPluginNavigator _navigator;
    private readonly IAnimePlaybackLauncher _playback;
    private readonly ILogger<TodayPage> _logger;
    private readonly HashSet<FrameworkElement> _reminderControls = [];
    private readonly DesktopDeckOverlay _plans;
    private readonly DesktopPageOperations _operations = new();
    private TodayPlanStackPanel? _planPanel;
    private CancellationTokenSource? _visit;
    private ContentDialog? _dialog;
    private bool _dialogOperation;
    private bool _showReminders;
    private int? _requestedPlan;
    private double _width;
    private double _scrollOffset;

    public TodayViewModel ViewModel { get; }
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;
    internal Task PendingOperation { get => _operations.Pending; private set => _operations.Track(value); }
    internal bool IsPlanDeckOpen => _plans.IsOpen;

    public TodayPage(IAnimeDataSource dataSource, TrackingService tracking, PersonalFeatureStateStore featureState,
        ActionCenterService actionCenter, PlanReminderCoordinator reminders, BrowseHistoryService browseHistory,
        ArchiveService archive, IPluginNavigator navigator, IAnimePlaybackLauncher playbackLauncher, ILogger<TodayPage> logger)
    {
        _actionCenter = actionCenter;
        _archive = archive;
        _reminders = reminders;
        _navigator = navigator;
        _playback = playbackLauncher;
        _logger = logger;
        ViewModel = new(dataSource, tracking, featureState, actionCenter, reminders, browseHistory, archive, logger);
        InitializeComponent();
        _plans = new(PlanCard, PlanVisual, PlanScroll, PlanStackPopup, PlanDismissCanvas, () => _planPanel, DesktopPlanMoreButton);
        _plans.Closed += (_, _) =>
        {
            _ = ViewModel.SetPlanDeckExpandedAsync(false);
            DesktopPlanMoreButton.Content = ViewModel.PlanStackHint;
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnPageKeyDown;
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        _requestedPlan = parameter as int?;
        if (_visit is not { } visit) return;
        var token = visit.Token;
        await PendingActivation;
        if (!token.IsCancellationRequested) await RevealPlanAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_visit is not null) return;
        _visit = new();
        ViewModel.PropertyChanged += OnViewModelChanged;
        DesktopPresentationPreferences.Changed += OnPreferencesChanged;
        _playback.AvailabilityChanged += OnPlaybackAvailabilityChanged;
        ViewModel.IsPlaybackAvailable = _playback.IsAvailable;
        UpdateReminderVisibility();
        PendingActivation = ActivateVisitAsync(_visit.Token);
    }

    private async Task ActivateVisitAsync(CancellationToken token)
    {
        await _operations.DrainAsync();
        if (!token.IsCancellationRequested) await ActivateAsync(token, reload: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_visit is not { } visit) return;
        _visit = null;
        _scrollOffset = PageScroll.VerticalOffset;
        _plans.Close();
        _dialog?.Hide();
        visit.Cancel();
        visit.Dispose();
        ViewModel.Theme.CancelLoad();
        ViewModel.SuspendPlanDetails();
        ViewModel.PropertyChanged -= OnViewModelChanged;
        DesktopPresentationPreferences.Changed -= OnPreferencesChanged;
        _playback.AvailabilityChanged -= OnPlaybackAvailabilityChanged;
        _reminderControls.Clear();
    }

    private async Task ActivateAsync(CancellationToken token, bool reload)
    {
        try
        {
            if (reload) await ViewModel.LoadAsync(token);
            else
            {
                ViewModel.Theme.ResumeIfNeeded();
                await ViewModel.RemoveBlockedEntriesAsync(token);
            }
            if (token.IsCancellationRequested) return;
            UpdateError();
            ApplyLayout();
            PageScroll.ChangeView(null, _scrollOffset, null, true);
            await RevealPlanAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 页面负责加载失败的最后提示。
        catch (Exception error)
        {
            _logger.LogError(error, "今天页读取失败。");
            if (!token.IsCancellationRequested) Notify("今天页加载失败，请按 F5 重试。", error: true);
        }
#pragma warning restore CA1031
    }

    private async Task RevealPlanAsync()
    {
        if (_requestedPlan is not { } id || _visit is not { } visit) return;
        _requestedPlan = null;
        var token = visit.Token;
        var index = ViewModel.Plans.ToList().FindIndex(entry => entry.Plan.AnimeId == id);
        if (index < 0) return;
        if (index < 3) { PlanCard.StartBringIntoView(); return; }
        await OpenPlansAsync();
        if (token.IsCancellationRequested || !_plans.IsOpen || _planPanel is null) return;
        PlanScroll.UpdateLayout();
        PlanScroll.ChangeView(null, _planPanel.Children.Take(index).Sum(child => child.DesiredSize.Height), null, true);
    }

    internal Task RefreshAsync()
    {
        if (_visit is null || !PendingActivation.IsCompleted) return PendingActivation;
        _plans.Close();
        return PendingActivation = ActivateAsync(_visit.Token, reload: true);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TodayViewModel.ErrorMessage)) UpdateError();
        if (args.PropertyName == nameof(TodayViewModel.Plans)) _plans.Close();
    }

    private void UpdateError()
    {
        ErrorInfoBar.Message = ViewModel.ErrorMessage ?? string.Empty;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
    }

    private void OnPreferencesChanged(object? sender, EventArgs args)
    {
        var changed = _showReminders != DesktopPresentationPreferences.ShowPlanReminders;
        UpdateReminderVisibility();
        if (changed) _ = RefreshAsync();
    }

    private void UpdateReminderVisibility()
    {
        _showReminders = DesktopPresentationPreferences.ShowPlanReminders;
        PlanFooter.Visibility = _showReminders ? Visibility.Visible : Visibility.Collapsed;
        foreach (var control in _reminderControls) control.Visibility = PlanFooter.Visibility;
    }

    private void OnPlaybackAvailabilityChanged(object? sender, EventArgs args)
    {
        var visit = _visit;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (visit is null || !ReferenceEquals(visit, _visit)) return;
            ViewModel.IsPlaybackAvailable = _playback.IsAvailable;
            _ = RefreshAsync();
        });
    }

    private void OnDesktopReminderLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement control) return;
        _reminderControls.Add(control);
        control.Visibility = DesktopPresentationPreferences.ShowPlanReminders ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnDesktopReminderUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement control) _reminderControls.Remove(control);
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Width <= 0 || Math.Abs(_width - args.NewSize.Width) < 0.5) return;
        _plans?.Close();
        _width = args.NewSize.Width;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var beside = _width >= 880;
        Grid.SetColumnSpan(BroadcastCard, beside ? 1 : 2);
        Grid.SetRow(PlanCard, beside ? 0 : 1);
        Grid.SetColumn(PlanCard, beside ? 1 : 0);
        Grid.SetColumnSpan(PlanCard, beside ? 1 : 2);
        PlanCard.Margin = new(0, beside ? 0 : 22, 0, 0);
        PlanSurface.BorderThickness = beside ? new(1, 0, 0, 0) : new(0);
        PlanSurface.Padding = beside ? new(16, 17, 0, 0) : new(0);
        Grid.SetRow(ThemeCard, beside ? 1 : 2);
        ThemeCard.Margin = new(0, 22, 0, 0);
        Grid.SetColumn(ThemeRows, beside ? 1 : 0);
        Grid.SetColumnSpan(ThemeRows, beside ? 1 : 2);
        Grid.SetRow(ThemeRows, beside ? 0 : 1);
        ThemeRows.Margin = new(0, beside ? 0 : 12, 0, 0);
        Grid.SetColumnSpan(DesktopThemeTitle, beside ? 1 : 2);
        Grid.SetRow(DesktopThemeStatus, beside ? 1 : 2);
        Grid.SetRow(PlaybackCard, beside ? 2 : 3);
        PlaybackCard.Margin = new(0, ViewModel.IsPlaybackAvailable ? 22 : 0, 0, 0);
    }

    internal async Task OpenPlansAsync()
    {
        if (_visit is not { } visit || !ViewModel.HasOverflowPlans || !_plans.Open()) return;
        DesktopPlanMoreButton.Content = "收起计划";
        var token = visit.Token;
        try { await ViewModel.SetPlanDeckExpandedAsync(true); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 补图失败时保留可以操作的计划。
        catch (Exception error)
        {
            _logger.LogWarning(error, "补番计划详情加载失败。");
            if (!token.IsCancellationRequested) Notify("部分计划封面暂时无法加载。", error: true);
        }
#pragma warning restore CA1031
    }

    private void OnPlanPanelLoaded(object sender, RoutedEventArgs args) => _planPanel = (TodayPlanStackPanel)sender;
    private void OnDesktopPlanMoreClick(object sender, RoutedEventArgs args)
    {
        if (_plans.IsOpen) _plans.Close();
        else PendingOperation = OpenPlansAsync();
    }
    private void OnPlanDeckTapped(object sender, TappedRoutedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, sender)) return;
        args.Handled = true;
        PendingOperation = OpenPlansAsync();
    }
    private void OnPlanDeckKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, sender) || args.Key is not (VirtualKey.Enter or VirtualKey.Space)) return;
        args.Handled = true;
        PendingOperation = OpenPlansAsync();
    }
    private void OnPageScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args) => _plans?.Close();
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.F5) return;
        args.Handled = true;
        _ = RefreshAsync();
    }
    private void OnRefreshClick(object sender, RoutedEventArgs args) => _ = RefreshAsync();
    public static string DesktopPlanDate(DateOnly? date) => date?.ToString("MM/dd", System.Globalization.CultureInfo.InvariantCulture) ?? "—";

    private void OnAnimeButtonClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Anime anime }) OpenDetails(anime.ID);
    }
    private void OnPlanDetailsClick(object sender, RoutedEventArgs args)
    {
        if (Plan(sender) is { } entry) OpenDetails(entry.Plan.AnimeId);
    }
    private void OnDesktopThemeCardClicked(object? sender, AnimeCardClickedEventArgs args) => OpenDetails(args.Anime.ID);
    private void OpenDetails(int animeId) { _plans.Close(); _navigator.Navigate(typeof(AnimeDetailPage), animeId); }
    private void OnCalendarClick(object sender, RoutedEventArgs args) => _navigator.Navigate(typeof(CurrentSeasonPage));
    private void OnPlaybackClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Anime anime } && _playback.IsAvailable)
            _ = RunOperationAsync(_ => _playback.LaunchAsync(new(anime.ID, anime.Title, anime.AlternateTitles)));
    }
    private void OnCoverLoaded(object sender, RoutedEventArgs args) => ConfigureCover(sender);
    private void OnCoverDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => ConfigureCover(sender);
    private void OnCoverUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image) ManagedImageLoader.Cancel(image);
    }
    private static void ConfigureCover(object sender)
    {
        if (sender is not Image { IsLoaded: true } image) return;
        if (image.Tag is Anime anime) ManagedImageLoader.ConfigureCover(image, anime.ID, anime.CoverURL, 52);
        else ManagedImageLoader.Cancel(image);
    }

    private static TodayPlanEntry? Plan(object sender) => sender is FrameworkElement { Tag: TodayPlanEntry entry } ? entry : null;
    private void Notify(string message, bool error = false)
    {
        if (DesktopFeedback.TryShow(message, error)) return;
        NotificationInfoBar.Message = message;
        NotificationInfoBar.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        NotificationInfoBar.IsOpen = true;
    }

    private Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_visit is not { } visit || _dialogOperation) return Task.CompletedTask;
        _dialogOperation = true;
        var operation = ExecuteOperationAsync(action, visit.Token);
        _operations.Track(operation);
        return operation;
    }

    private async Task ExecuteOperationAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        try { await action(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 事件边界报告失败，保留操作门直到已接纳写入收尾。
        catch (Exception error)
        {
            _logger.LogError(error, "今天页操作失败。");
            if (!token.IsCancellationRequested) Notify($"操作失败：{error.Message}", error: true);
        }
#pragma warning restore CA1031
        finally { _dialogOperation = false; }
    }

    internal async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, CancellationToken token)
    {
        if (token.IsCancellationRequested || _visit is not { } visit || XamlRoot is null || _dialog is not null) return ContentDialogResult.None;
        _plans.Close();
        _dialog = dialog;
        dialog.XamlRoot = XamlRoot;
        try
        {
            var result = await dialog.ShowAsync();
            return token.IsCancellationRequested || !ReferenceEquals(visit, _visit) ? ContentDialogResult.None : result;
        }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }
}
