using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class TodayPage
{
    private void OnClearBrowseHistoryClick(object sender, RoutedEventArgs args)
        => _ = RunOperationAsync(async token =>
        {
            var dialog = Confirm("清空最近浏览", "确定要清空所有浏览记录吗？", "确认清空");
            if (await ShowDialogAsync(dialog, token) == ContentDialogResult.Primary)
                await ViewModel.ClearBrowseHistoryCommand.ExecuteAsync(null);
        });

    private void OnEditPlanClick(object sender, RoutedEventArgs args)
    {
        if (Plan(sender) is { } entry) _ = RunOperationAsync(token => EditPlanAsync(entry, token));
    }

    private async Task EditPlanAsync(TodayPlanEntry entry, CancellationToken token)
    {
        var priority = new ComboBox
        {
            Header = "优先级", SelectedIndex = (int)entry.Plan.Priority,
            ItemsSource = new[] { "低", "普通", "高", "最高" },
        };
        var useDate = new CheckBox { Content = "设置目标日期", IsChecked = entry.Plan.TargetStartDate.HasValue };
        var date = new CalendarDatePicker
        {
            Header = "目标开始日期", IsEnabled = useDate.IsChecked == true,
            Date = entry.Plan.TargetStartDate is { } target ? new DateTimeOffset(target.ToDateTime(TimeOnly.MinValue)) : null,
        };
        useDate.Checked += (_, _) => date.IsEnabled = true;
        useDate.Unchecked += (_, _) => date.IsEnabled = false;
        var order = new NumberBox
        {
            Header = "排序（较小的排在前面）", Minimum = 0, Maximum = 10000, Value = entry.Plan.SortOrder,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(priority); panel.Children.Add(useDate); panel.Children.Add(date); panel.Children.Add(order);
        var dialog = Confirm($"调整《{entry.Title}》的计划", panel, "保存");
        if (await ShowDialogAsync(dialog, token) != ContentDialogResult.Primary) return;

        DateOnly? selectedDate = useDate.IsChecked == true && date.Date is { } value
            ? DateOnly.FromDateTime(value.LocalDateTime) : null;
        // 确认后的整项保存使用服务自身的写入生命周期；离页只取消随后展示。
        await _actionCenter.UpsertPlanAsync(entry.Plan.AnimeId, entry.Plan.TitleSnapshot,
            (AnimePlanPriority)Math.Clamp(priority.SelectedIndex, 0, 3), selectedDate,
            double.IsFinite(order.Value) ? (int)order.Value : entry.Plan.SortOrder);
        if (await _actionCenter.GetPlanAsync(entry.Plan.AnimeId) is { } updated)
            await _reminders.RescheduleAnimeAsync(updated);
        if (!token.IsCancellationRequested) await ActivateAsync(token, reload: true);
    }

    private void OnAddReminderClick(object sender, RoutedEventArgs args)
    {
        if (Plan(sender) is { } entry) _ = RunOperationAsync(token => AddReminderAsync(entry, token));
    }

    private async Task AddReminderAsync(TodayPlanEntry entry, CancellationToken token)
    {
        if (!_reminders.NotificationsAvailable) { Notify("系统通知不可用，请先检查通知设置。", error: true); return; }
        var kind = new ComboBox
        {
            Header = "提醒类型", ItemsSource = new[] { "相对目标日期", "自定义日期时间" },
            SelectedIndex = entry.Plan.TargetStartDate is null ? 1 : 0,
        };
        var days = new NumberBox
        {
            Header = "提前天数", Minimum = 0, Maximum = 365, Value = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var date = new CalendarDatePicker { Header = "提醒日期", Date = DateTimeOffset.Now.AddDays(1) };
        var time = new TimePicker { Header = "提醒时间", Time = new(20, 0, 0) };
        void UpdateFields()
        {
            days.Visibility = kind.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            date.Visibility = kind.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        kind.SelectionChanged += (_, _) => UpdateFields();
        UpdateFields();
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(kind); panel.Children.Add(days); panel.Children.Add(date); panel.Children.Add(time);
        if (await ShowDialogAsync(Confirm($"添加《{entry.Title}》提醒", panel, "添加"), token) != ContentDialogResult.Primary) return;
        try
        {
            if (kind.SelectedIndex == 0)
                await _reminders.AddRelativeReminderAsync(entry.Plan, PlanReminderCoordinator.GetRelativeDayOffset(days.Value), TimeOnly.FromTimeSpan(time.Time));
            else if (date.Date is { } selected)
                await _reminders.AddAbsoluteReminderAsync(entry.Plan, new DateTimeOffset(selected.Date.Add(time.Time)));
        }
        catch (InvalidOperationException error)
        {
            if (!token.IsCancellationRequested) Notify(error.Message, error: true);
            return;
        }
        if (!token.IsCancellationRequested) await ActivateAsync(token, reload: true);
    }

    private void OnManageRemindersClick(object sender, RoutedEventArgs args)
    {
        if (Plan(sender) is { } entry) _ = RunOperationAsync(async token =>
        {
            var reminders = await _actionCenter.GetRemindersAsync(entry.Plan.AnimeId, PlanReminderState.Pending, token);
            if (token.IsCancellationRequested) return;
            if (reminders.Count == 0) { Notify("这条计划当前没有待处理提醒。"); return; }
            var list = new ListView
            {
                ItemsSource = reminders.Select(reminder => new ReminderChoice(reminder)).ToArray(),
                SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320,
            };
            if (await ShowDialogAsync(Confirm($"管理《{entry.Title}》提醒", list, "删除所选"), token) != ContentDialogResult.Primary
                || list.SelectedItem is not ReminderChoice choice) return;
            await _reminders.RemoveReminderAsync(choice.Reminder);
            if (!token.IsCancellationRequested) await ActivateAsync(token, reload: true);
        });
    }

    private void OnThemePrimaryClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: TodayThemeItem item }) return;
        _ = RunOperationAsync(async token =>
        {
            if (item.RowKind == TodayThemeRowKind.Rate)
            {
                if (await _archive.GetArchiveAsync(item.Anime.ID) is null)
                    await _archive.UpsertArchiveAsync(item.Anime.ID, item.Anime.Title, null, string.Empty);
                if (!token.IsCancellationRequested) _navigator.Navigate(typeof(ArchivePage), item.Anime.ID);
            }
            else if (item.PrimaryStatus is { } status) await ApplyThemeStatusAsync(item, status, token);
        });
    }

    private void OnThemeSecondaryClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: TodayThemeItem { SecondaryStatus: { } status } item })
            _ = RunOperationAsync(token => ApplyThemeStatusAsync(item, status, token));
    }

    private async Task ApplyThemeStatusAsync(TodayThemeItem item, AnimeTrackingStatus status, CancellationToken token)
    {
        if (await ViewModel.Theme.ApplyStatusAsync(item, status) && !token.IsCancellationRequested)
            await ViewModel.RefreshAfterStatusChangeAsync(token);
    }

    private void OnNotificationSettingsClick(object sender, RoutedEventArgs args)
        => _ = RunOperationAsync(_ => _reminders.OpenNotificationSettingsAsync());

    private static ContentDialog Confirm(string title, object content, string primary) => new()
    {
        Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
    };

    private sealed record ReminderChoice(PlanReminder Reminder)
    {
        public override string ToString() => $"{Reminder.ScheduledFor.LocalDateTime:g}  "
            + (Reminder.Kind == PlanReminderKind.Absolute ? "自定义时间" : "相对目标日期");
    }
}
