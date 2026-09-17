using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class TodayPage : Page, INavigationAware
{
    // 本周条带放到今日放送右侧所需的最小内容宽度，以及条带在右侧时的宽度（7 × 92 + 6 × 8）。
    private const double WeekStripBesideWidth = 1180;
    private const double WeekStripWidth = 692;
    // 最近观看与补番计划并排所需的最小内容宽度。
    private const double ActivitySideBySideWidth = 1000;
    // 补番计划独占整行时，低于这个宽度也改用紧凑行。
    private const double WidePlanRowWidth = 640;
    // 页面上最大的封面宽度，封面按它解码。
    private const double CoverDecodeWidth = 60;
    private readonly ActionCenterService _actionCenter;
    private readonly PlanReminderCoordinator _reminders;
    private readonly IPluginNavigator _navigator;
    private readonly IAnimePlaybackLauncher _playbackLauncher;
    private bool _isPlaybackAvailabilitySubscribed;
    private double _contentWidth = double.PositiveInfinity;
    private CancellationTokenSource? _loadCancellation;

    public TodayPage(
        IAnimeDataSource dataSource,
        TrackingService tracking,
        ActionCenterService actionCenter,
        PlanReminderCoordinator reminders,
        BrowseHistoryService browseHistory,
        IPluginNavigator navigator,
        IAnimePlaybackLauncher playbackLauncher)
    {
        _actionCenter = actionCenter;
        _reminders = reminders;
        _navigator = navigator;
        _playbackLauncher = playbackLauncher;
        ViewModel = new TodayViewModel(
            dataSource,
            tracking,
            actionCenter,
            reminders,
            browseHistory);
        InitializeComponent();
        ViewModel.IsPlaybackAvailable = playbackLauncher.IsAvailable;
        ApplyLayout();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TodayViewModel.ErrorMessage))
            {
                ErrorInfoBar.Message = ViewModel.ErrorMessage;
                ErrorInfoBar.IsOpen =
                    !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
            }
            else if (args.PropertyName
                == nameof(TodayViewModel.IsPlaybackAvailable))
            {
                ApplyLayout();
            }
        };
    }

    public TodayViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RemoveBlockedEntriesAsync();
        }
#pragma warning disable CA1031 // 可见性刷新失败不应阻止页面加载
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TodayPage] RemoveBlockedEntriesAsync failed: {ex.Message}");
        }
#pragma warning restore CA1031

        if (_isPlaybackAvailabilitySubscribed)
        {
            return;
        }

        _playbackLauncher.AvailabilityChanged +=
            OnPlaybackAvailabilityChanged;
        _isPlaybackAvailabilitySubscribed = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        if (!_isPlaybackAvailabilitySubscribed)
        {
            return;
        }

        _playbackLauncher.AvailabilityChanged -=
            OnPlaybackAvailabilityChanged;
        _isPlaybackAvailabilitySubscribed = false;
    }

    private void OnPlaybackAvailabilityChanged(
        object? sender,
        EventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.IsPlaybackAvailable =
                _playbackLauncher.IsAvailable;
            _ = ReloadSafelyAsync();
        });

    public async Task OnNavigatedToAsync(object? parameter)
    {
        await ReloadAsync();
        if (parameter is int animeId
            && ViewModel.Plans.FirstOrDefault(
                item => item.Plan.AnimeId == animeId) is { } entry)
        {
            PlanList.ScrollIntoView(entry);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await ReloadSafelyAsync();

    private async Task ReloadSafelyAsync()
    {
        try
        {
            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // UI 事件边界将恢复性错误转换为页面提示。
        catch (Exception ex)
        {
            ShowNotification(
                $"今天页刷新失败：{ex.Message}",
                InfoBarSeverity.Error);
        }
#pragma warning restore CA1031
    }

    private async Task ReloadAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        await ViewModel.LoadAsync(_loadCancellation.Token);
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _contentWidth = e.NewSize.Width;
        ApplyLayout();
    }

    /// <summary>
    /// 按内容宽度调整布局：本周条带宽时在今日放送右侧、窄时移到下方；
    /// 播放器可用且宽度足够时最近观看与补番计划并排，否则上下排列，
    /// 播放器不可用时补番计划独占整行；补番计划并排或整行过窄时，
    /// 操作按钮换到标题下方。
    /// </summary>
    private void ApplyLayout()
    {
        var stripBeside = _contentWidth >= WeekStripBesideWidth;
        Grid.SetColumnSpan(TodayContent, stripBeside ? 1 : 2);
        Grid.SetRow(WeekStrip, stripBeside ? 1 : 2);
        Grid.SetColumn(WeekStrip, stripBeside ? 1 : 0);
        Grid.SetColumnSpan(WeekStrip, stripBeside ? 1 : 2);
        WeekStrip.Width = stripBeside ? WeekStripWidth : double.NaN;
        WeekStrip.Margin = stripBeside
            ? new Thickness(0)
            : new Thickness(0, 16, 0, 0);

        var hasPlayback = ViewModel.IsPlaybackAvailable;
        var sideBySide = hasPlayback
            && _contentWidth >= ActivitySideBySideWidth;
        Grid.SetColumnSpan(PlaybackCard, sideBySide ? 1 : 2);
        Grid.SetRow(PlanCard, hasPlayback && !sideBySide ? 1 : 0);
        Grid.SetColumn(PlanCard, sideBySide ? 1 : 0);
        Grid.SetColumnSpan(PlanCard, sideBySide ? 1 : 2);
        ActivityGrid.RowSpacing = hasPlayback && !sideBySide ? 16 : 0;

        var wideRows = !sideBySide && _contentWidth >= WidePlanRowWidth;
        var template = (DataTemplate)Resources[
            wideRows ? "PlanRowWideTemplate" : "PlanRowCompactTemplate"];
        if (!ReferenceEquals(PlanList.ItemTemplate, template))
        {
            PlanList.ItemTemplate = template;
        }
    }

    private void OnAnimeButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Anime anime })
        {
            _navigator.Navigate(typeof(AnimeDetailPage), anime.ID);
        }
    }

    private void OnPlanDetailsClick(object sender, RoutedEventArgs e)
    {
        if (TryGetPlan(sender, out var entry))
        {
            _navigator.Navigate(typeof(AnimeDetailPage), entry.Plan.AnimeId);
        }
    }

    private void OnCalendarClick(object sender, RoutedEventArgs e)
        => _navigator.Navigate(typeof(CurrentSeasonPage));

    private async void OnPlaybackClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Anime anime }
            || !_playbackLauncher.IsAvailable)
        {
            return;
        }

        try
        {
            // 播放上下文不含集数与位置，所以这里是“打开播放器”，不是“继续播放”。
            await _playbackLauncher.LaunchAsync(
                new AnimePlaybackContext(
                    anime.ID,
                    anime.Title,
                    anime.AlternateTitles));
        }
#pragma warning disable CA1031 // Optional playback must not break the Today page.
        catch (Exception ex)
        {
            ShowNotification(
                $"无法打开在线播放器：{ex.Message}",
                InfoBarSeverity.Error);
        }
#pragma warning restore CA1031
    }

    private void OnCoverLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            ConfigureCover(image);
        }
    }

    private void OnCoverDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (sender is Image image && image.IsLoaded)
        {
            ConfigureCover(image);
        }
    }

    private void OnCoverUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            ManagedImageLoader.Cancel(image);
        }
    }

    private static void ConfigureCover(Image image)
    {
        // 列表容器会复用，优先以当前 DataContext 为准，Tag 只作兜底。
        var anime = image.DataContext switch
        {
            TodayAnimeEntry entry => entry.Anime,
            TodayPlanEntry entry => entry.Anime,
            TodayBrowseEntry entry => entry.Anime,
            Anime item => item,
            _ => image.Tag as Anime,
        };
        if (anime is null)
        {
            ManagedImageLoader.Cancel(image);
            return;
        }

        ManagedImageLoader.ConfigureCover(
            image,
            anime.ID,
            anime.CoverURL,
            CoverDecodeWidth);
    }

    private async void OnClearBrowseHistoryClick(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空最近浏览",
            Content = "确定要清空所有浏览记录吗？",
            PrimaryButtonText = "确认清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearBrowseHistoryCommand.ExecuteAsync(null);
        }
    }

    private async void OnStartPlanClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryGetPlan(sender, out var entry))
        {
            return;
        }

        try
        {
            await _actionCenter.StartPlanAsync(entry.Plan.AnimeId);
        }
        catch (Exception ex) when (
            ex is Microsoft.Data.Sqlite.SqliteException
            or InvalidOperationException)
        {
            ShowNotification(
                $"开始补番失败：{ex.Message}",
                InfoBarSeverity.Error);
            return;
        }

        ShowNotification(
            "已开始补番，计划已归档，待发送提醒已取消。",
            InfoBarSeverity.Success);
        await ReloadSafelyAsync();
    }

    private async void OnEditPlanClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetPlan(sender, out var entry))
        {
            return;
        }

        var priority = new ComboBox
        {
            Header = "优先级",
            ItemsSource = Enum.GetValues<AnimePlanPriority>(),
            SelectedItem = entry.Plan.Priority,
        };
        var useTargetDate = new CheckBox
        {
            Content = "设置目标日期",
            IsChecked = entry.Plan.TargetStartDate is not null,
        };
        var targetDate = new CalendarDatePicker
        {
            Header = "目标开始日期",
            IsEnabled = useTargetDate.IsChecked == true,
            Date = entry.Plan.TargetStartDate is { } date
                ? new DateTimeOffset(
                    date.ToDateTime(TimeOnly.MinValue))
                : null,
        };
        useTargetDate.Checked += (_, _) => targetDate.IsEnabled = true;
        useTargetDate.Unchecked += (_, _) => targetDate.IsEnabled = false;
        var sortOrder = new NumberBox
        {
            Header = "手动排序值（较小的排在前面）",
            Minimum = 0,
            Maximum = 10000,
            Value = entry.Plan.SortOrder,
            SpinButtonPlacementMode =
                NumberBoxSpinButtonPlacementMode.Compact,
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(priority);
        panel.Children.Add(useTargetDate);
        panel.Children.Add(targetDate);
        panel.Children.Add(sortOrder);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"编辑《{entry.Title}》",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        DateOnly? dateValue = useTargetDate.IsChecked == true
            && targetDate.Date is { } selectedDate
            ? DateOnly.FromDateTime(selectedDate.LocalDateTime)
            : null;
        await _actionCenter.UpsertPlanAsync(
            entry.Plan.AnimeId,
            entry.Plan.TitleSnapshot,
            priority.SelectedItem is AnimePlanPriority selectedPriority
                ? selectedPriority
                : AnimePlanPriority.Normal,
            dateValue,
            double.IsNaN(sortOrder.Value)
                ? entry.Plan.SortOrder
                : (int)sortOrder.Value);
        var updated = await _actionCenter.GetPlanAsync(
            entry.Plan.AnimeId);
        if (updated is not null)
        {
            await _reminders.RescheduleAnimeAsync(updated);
        }
        await ReloadSafelyAsync();
    }

    private async void OnAddReminderClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryGetPlan(sender, out var entry))
        {
            return;
        }

        var kind = new ComboBox
        {
            Header = "提醒类型",
            Items =
            {
                "相对目标日期",
                "自定义日期时间",
            },
            SelectedIndex = entry.Plan.TargetStartDate is null ? 1 : 0,
        };
        var days = new NumberBox
        {
            Header = "提前天数",
            Minimum = 0,
            Maximum = 365,
            Value = 1,
            SpinButtonPlacementMode =
                NumberBoxSpinButtonPlacementMode.Compact,
        };
        var date = new CalendarDatePicker
        {
            Header = "提醒日期",
            Date = DateTimeOffset.Now.AddDays(1),
        };
        var time = new TimePicker
        {
            Header = "提醒时间",
            Time = new TimeSpan(20, 0, 0),
        };
        void UpdateFields()
        {
            var relative = kind.SelectedIndex == 0;
            days.Visibility =
                relative ? Visibility.Visible : Visibility.Collapsed;
            date.Visibility =
                relative ? Visibility.Collapsed : Visibility.Visible;
        }
        kind.SelectionChanged += (_, _) => UpdateFields();
        UpdateFields();
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(kind);
        panel.Children.Add(days);
        panel.Children.Add(date);
        panel.Children.Add(time);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"添加《{entry.Title}》提醒",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (kind.SelectedIndex == 0)
            {
                await _reminders.AddRelativeReminderAsync(
                    entry.Plan,
                    PlanReminderCoordinator.GetRelativeDayOffset(days.Value),
                    TimeOnly.FromTimeSpan(time.Time));
            }
            else if (date.Date is { } selectedDate)
            {
                var local = selectedDate.Date.Add(time.Time);
                await _reminders.AddAbsoluteReminderAsync(
                    entry.Plan,
                    new DateTimeOffset(local));
            }
        }
        catch (InvalidOperationException ex)
        {
            ShowNotification(ex.Message, InfoBarSeverity.Warning);
            return;
        }

        await ReloadSafelyAsync();
    }

    private async void OnManageRemindersClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryGetPlan(sender, out var entry))
        {
            return;
        }

        var reminders = await _actionCenter.GetRemindersAsync(
            entry.Plan.AnimeId,
            PlanReminderState.Pending);
        if (reminders.Count == 0)
        {
            ShowNotification("这条计划当前没有待处理提醒。");
            return;
        }

        var choices = reminders.Select(reminder => new ReminderChoice(
            reminder,
            $"{reminder.ScheduledFor.LocalDateTime:g} · "
                + (reminder.Kind == PlanReminderKind.Absolute
                    ? "自定义时间"
                    : "相对目标日期"))).ToList();
        var list = new ListView
        {
            ItemsSource = choices,
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 320,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"管理《{entry.Title}》提醒",
            Content = list,
            PrimaryButtonText = "删除所选",
            CloseButtonText = "关闭",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && list.SelectedItem is ReminderChoice choice)
        {
            await _reminders.RemoveReminderAsync(choice.Reminder);
            await ReloadSafelyAsync();
        }
    }

    private async void OnNotificationSettingsClick(
        object sender,
        RoutedEventArgs e)
        => await _reminders.OpenNotificationSettingsAsync();

    /// <summary>
    /// 操作直接取自所在行，不再依赖列表选中项。
    /// 菜单项不在可视树里，Tag 取不到时回退到 DataContext。
    /// </summary>
    private static bool TryGetPlan(
        object sender,
        out TodayPlanEntry entry)
    {
        switch (sender)
        {
            case FrameworkElement { Tag: TodayPlanEntry tagged }:
                entry = tagged;
                return true;
            case FrameworkElement { DataContext: TodayPlanEntry context }:
                entry = context;
                return true;
            default:
                entry = null!;
                return false;
        }
    }

    private void ShowNotification(
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        NotificationInfoBar.Severity = severity;
        NotificationInfoBar.Message = message;
        NotificationInfoBar.IsOpen = true;
    }

    private sealed record ReminderChoice(
        PlanReminder Reminder,
        string DisplayText)
    {
        public override string ToString() => DisplayText;
    }
}
