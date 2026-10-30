using AniMeido.Contracts;
using AniMeido.Contracts.Notifications;
using AniMeido.Plugin.Base.Models;

namespace AniMeido.Plugin.Base.Services;

public sealed class PlanReminderCoordinator : IDisposable
{
    public const string NotificationCategory = "anime-plan";
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(24);
    private readonly ActionCenterService _actionCenter;
    private readonly IAppNotificationService _notifications;
    private readonly IPlanReminderNavigation _navigator;
    private readonly IDisposable _activationRegistration;
    private bool _disposed;

    public PlanReminderCoordinator(
        ActionCenterService actionCenter,
        IAppNotificationService notifications,
        IPlanReminderNavigation navigator)
    {
        _actionCenter = actionCenter;
        _notifications = notifications;
        _navigator = navigator;
        _activationRegistration =
            notifications.RegisterActivationHandler(
                NotificationCategory,
                HandleActivationAsync);
    }

    public bool NotificationsAvailable =>
        _notifications.IsSupported && _notifications.NotificationsEnabled;

    internal static int GetRelativeDayOffset(double daysBefore)
    {
        if (!double.IsFinite(daysBefore)
            || daysBefore < 0
            || daysBefore > 365
            || daysBefore != Math.Truncate(daysBefore))
        {
            throw new InvalidOperationException(
                "提前天数必须是 0 到 365 之间的整数。");
        }

        return -(int)daysBefore;
    }

    public async Task<PlanReminder> AddRelativeReminderAsync(
        AnimePlan plan,
        int relativeDays,
        TimeOnly timeOfDay,
        CancellationToken cancellationToken = default)
    {
        using var operation = _actionCenter.Access.BeginWrite();
        if (plan.TargetStartDate is null)
        {
            throw new InvalidOperationException(
                "相对提醒需要先设置目标日期。");
        }

        var scheduled = ToLocalDateTimeOffset(
            plan.TargetStartDate.Value.AddDays(relativeDays),
            timeOfDay);
        var reminder = CreateReminder(
            plan.AnimeId,
            PlanReminderKind.RelativeToTargetDate,
            relativeDays,
            timeOfDay,
            absoluteAt: null,
            scheduled);
        await SaveAndScheduleAsync(plan, reminder, cancellationToken);
        return reminder;
    }

    public async Task<PlanReminder> AddAbsoluteReminderAsync(
        AnimePlan plan,
        DateTimeOffset deliveryTime,
        CancellationToken cancellationToken = default)
    {
        using var operation = _actionCenter.Access.BeginWrite();
        var reminder = CreateReminder(
            plan.AnimeId,
            PlanReminderKind.Absolute,
            relativeDays: null,
            timeOfDay: null,
            deliveryTime,
            deliveryTime);
        await SaveAndScheduleAsync(plan, reminder, cancellationToken);
        return reminder;
    }

    public async Task RemoveReminderAsync(
        PlanReminder reminder,
        CancellationToken cancellationToken = default)
    {
        using var operation = _actionCenter.Access.BeginWrite();
        await _actionCenter.CancelReminderAsync(
            reminder.ReminderId,
            cancellationToken);
        await _notifications.CancelAsync(
            GetNotificationGroup(reminder.AnimeId),
            reminder.ReminderId,
            cancellationToken);
    }

    public async Task RescheduleAnimeAsync(
        AnimePlan plan,
        CancellationToken cancellationToken = default)
    {
        using var operation = _actionCenter.Access.BeginWrite();
        var reminders = await _actionCenter.GetRemindersAsync(
            plan.AnimeId,
            PlanReminderState.Pending,
            cancellationToken);
        foreach (var reminder in reminders)
        {
            var updated = reminder;
            if (reminder.Kind == PlanReminderKind.RelativeToTargetDate)
            {
                if (plan.TargetStartDate is null
                    || reminder.RelativeDays is null
                    || reminder.TimeOfDay is null)
                {
                    await RemoveReminderAsync(reminder, cancellationToken);
                    continue;
                }

                updated = reminder with
                {
                    ScheduledFor = ToLocalDateTimeOffset(
                        plan.TargetStartDate.Value.AddDays(
                            reminder.RelativeDays.Value),
                        reminder.TimeOfDay.Value),
                    CatchUpSentAt = null,
                };
                await _actionCenter.AddReminderAsync(
                    updated,
                    cancellationToken);
            }

            if (updated.ScheduledFor > DateTimeOffset.Now)
            {
                await ScheduleAsync(plan, updated, cancellationToken);
            }
        }
    }

    public async Task ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = _actionCenter.Access.BeginWrite();
        var plans = (await _actionCenter.GetPlansAsync(
            cancellationToken: cancellationToken))
            .ToDictionary(plan => plan.AnimeId);
        var pending = await _actionCenter.GetRemindersAsync(
            state: PlanReminderState.Pending,
            cancellationToken: cancellationToken);
        foreach (var reminder in pending.Where(
            item => item.ScheduledFor > DateTimeOffset.Now))
        {
            if (plans.TryGetValue(reminder.AnimeId, out var plan))
            {
                await ScheduleAsync(plan, reminder, cancellationToken);
            }
        }

        var catchUps =
            await _actionCenter.GetRecentUnprocessedRemindersAsync(
                DateTimeOffset.Now,
                CatchUpWindow,
                cancellationToken);
        foreach (var reminder in catchUps)
        {
            if (!plans.TryGetValue(reminder.AnimeId, out var plan))
            {
                continue;
            }

            await ScheduleAsync(
                plan,
                reminder with { ScheduledFor = DateTimeOffset.Now },
                cancellationToken);
            await _actionCenter.MarkReminderCatchUpSentAsync(
                reminder.ReminderId,
                cancellationToken);
        }
    }

    public Task OpenNotificationSettingsAsync()
        => _notifications.OpenNotificationSettingsAsync();

    private async Task SaveAndScheduleAsync(
        AnimePlan plan,
        PlanReminder reminder,
        CancellationToken cancellationToken)
    {
        if (reminder.ScheduledFor <= DateTimeOffset.Now)
        {
            throw new InvalidOperationException("提醒时间必须晚于当前时间。");
        }

        await _actionCenter.AddReminderAsync(
            reminder,
            cancellationToken);
        try
        {
            await ScheduleAsync(plan, reminder, cancellationToken);
        }
        catch
        {
            await _actionCenter.CancelReminderAsync(
                reminder.ReminderId,
                CancellationToken.None);
            throw;
        }
    }

    private Task ScheduleAsync(
        AnimePlan plan,
        PlanReminder reminder,
        CancellationToken cancellationToken)
        => _notifications.ScheduleAsync(
            new AppNotificationRequest(
                NotificationCategory,
                GetNotificationGroup(plan.AnimeId),
                reminder.ReminderId,
                reminder.ScheduledFor,
                "补番计划提醒",
                $"《{plan.TitleSnapshot}》已经到计划时间。",
                new Dictionary<string, string>
                {
                    ["animeId"] = plan.AnimeId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["reminderId"] = reminder.ReminderId,
                },
                [
                    new AppNotificationAction("稍后 1 小时", "snooze"),
                ]),
            cancellationToken);

    private async Task HandleActivationAsync(
        AppNotificationActivation activation,
        CancellationToken cancellationToken)
    {
        if (!TryGetInt(activation.Arguments, "animeId", out var animeId))
        {
            return;
        }

        activation.Arguments.TryGetValue(
            "reminderId",
            out var reminderId);
        using var operation = string.IsNullOrWhiteSpace(reminderId)
            ? null
            : _actionCenter.Access.BeginWrite();
        switch (activation.Action)
        {
            case "snooze":
                if (string.IsNullOrWhiteSpace(reminderId)
                    || !await _actionCenter.TryMarkReminderHandledAsync(
                        reminderId,
                        cancellationToken))
                {
                    break;
                }

                var plan = await _actionCenter.GetPlanAsync(
                    animeId,
                    cancellationToken);
                if (plan is not null)
                {
                    await AddAbsoluteReminderAsync(
                        plan,
                        DateTimeOffset.Now.AddHours(1),
                        cancellationToken);
                }
                break;

            default:
                if (!string.IsNullOrWhiteSpace(reminderId))
                {
                    await _actionCenter.TryMarkReminderHandledAsync(
                        reminderId,
                        cancellationToken);
                }
                break;
        }

        _navigator.OpenPlan(animeId);
    }

    private static PlanReminder CreateReminder(
        int animeId,
        PlanReminderKind kind,
        int? relativeDays,
        TimeOnly? timeOfDay,
        DateTimeOffset? absoluteAt,
        DateTimeOffset scheduled)
        => new(
            Guid.NewGuid().ToString("N"),
            animeId,
            kind,
            relativeDays,
            timeOfDay,
            absoluteAt,
            scheduled,
            PlanReminderState.Pending,
            CatchUpSentAt: null,
            HandledAt: null);

    private static DateTimeOffset ToLocalDateTimeOffset(
        DateOnly date,
        TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(
            local,
            TimeZoneInfo.Local.GetUtcOffset(local));
    }

    internal static string GetNotificationGroup(int animeId)
        => $"anime-plan-{animeId}";

    private static bool TryGetInt(
        IReadOnlyDictionary<string, string> arguments,
        string key,
        out int value)
    {
        value = 0;
        return arguments.TryGetValue(key, out var text)
            && int.TryParse(
                text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activationRegistration.Dispose();
    }
}
