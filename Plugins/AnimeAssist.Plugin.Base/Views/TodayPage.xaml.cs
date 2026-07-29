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
    private readonly ActionCenterService _actionCenter;
    private readonly PlanReminderCoordinator _reminders;
    private readonly IPluginNavigator _navigator;
    private readonly IAnimePlaybackLauncher _playbackLauncher;
    private bool _isPlaybackAvailabilitySubscribed;

    public TodayPage(
        IAnimeDataSource dataSource,
        TrackingService tracking,
        ActionCenterService actionCenter,
        PlanReminderCoordinator reminders,
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
            reminders);
        NotificationSettingsButton = new Button
        {
            Content = "通知设置",
        };
        NotificationSettingsButton.Click += OnNotificationSettingsClick;
        InitializeComponent();
        ViewModel.IsPlaybackAvailable = playbackLauncher.IsAvailable;
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
                == nameof(TodayViewModel.NotificationMessage))
            {
                NotificationInfoBar.Message =
                    ViewModel.NotificationMessage;
                NotificationInfoBar.IsOpen = !string.IsNullOrWhiteSpace(
                    ViewModel.NotificationMessage);
            }
        };
    }

    public TodayViewModel ViewModel { get; }

    public Button NotificationSettingsButton { get; }

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
        => DispatcherQueue.TryEnqueue(async () =>
        {
            ViewModel.IsPlaybackAvailable =
                _playbackLauncher.IsAvailable;
            await ViewModel.LoadAsync();
        });

    public async Task OnNavigatedToAsync(object? parameter)
    {
        await ViewModel.LoadAsync();
        if (parameter is int animeId)
        {
            PlanList.SelectedItem = ViewModel.Plans.FirstOrDefault(
                item => item.Plan.AnimeId == animeId);
            if (PlanList.SelectedItem is not null)
            {
                PlanList.ScrollIntoView(PlanList.SelectedItem);
            }
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await ViewModel.LoadAsync();

    private void OnAnimeCardClicked(
        object? sender,
        Views.Controls.AnimeCardClickedEventArgs e)
        => _navigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);

    private void OnAnimeEntryClick(
        object sender,
        ItemClickEventArgs e)
    {
        if (e.ClickedItem is TodayAnimeEntry entry)
        {
            _navigator.Navigate(typeof(AnimeDetailPage), entry.Anime.ID);
        }
    }

    private async void OnEditPlanClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedPlan(out var entry))
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
        await ViewModel.LoadAsync();
    }

    private async void OnAddReminderClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryGetSelectedPlan(out var entry))
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
            ShowNotification(ex.Message);
        }
    }

    private async void OnStartPlanClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryGetSelectedPlan(out var entry))
        {
            return;
        }

        await _actionCenter.StartPlanAsync(entry.Plan.AnimeId);
        await ViewModel.LoadAsync();
    }

    private async void OnManageRemindersClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryGetSelectedPlan(out var entry))
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
            await ViewModel.LoadAsync();
        }
    }

    private void OnSmartListsClick(object sender, RoutedEventArgs e)
        => _navigator.Navigate(typeof(SmartListsPage));

    private async void OnNotificationSettingsClick(
        object sender,
        RoutedEventArgs e)
        => await _reminders.OpenNotificationSettingsAsync();

    private bool TryGetSelectedPlan(
        out TodayPlanEntry entry)
    {
        if (PlanList.SelectedItem is TodayPlanEntry selected)
        {
            entry = selected;
            return true;
        }

        entry = null!;
        ShowNotification("请先选择一条补番计划。");
        return false;
    }

    private void ShowNotification(string message)
    {
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
