namespace AniMeido.Contracts.Notifications;

/// <summary>
/// App-provided capability for local Windows notifications.
/// The contract intentionally contains no WinUI or Windows notification types.
/// </summary>
public interface IAppNotificationService
{
    bool IsSupported { get; }

    bool NotificationsEnabled { get; }

    Task ScheduleAsync(
        AppNotificationRequest request,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        string group,
        string tag,
        CancellationToken cancellationToken = default);

    Task CancelGroupAsync(
        string group,
        CancellationToken cancellationToken = default);

    IDisposable RegisterActivationHandler(
        string category,
        Func<AppNotificationActivation, CancellationToken, Task> handler);

    Task OpenNotificationSettingsAsync();
}

public sealed record AppNotificationRequest(
    string Category,
    string Group,
    string Tag,
    DateTimeOffset DeliveryTime,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Arguments,
    IReadOnlyList<AppNotificationAction> Actions);

public sealed record AppNotificationAction(
    string Label,
    string Action,
    IReadOnlyDictionary<string, string>? Arguments = null);

public sealed record AppNotificationActivation(
    string Category,
    string Action,
    IReadOnlyDictionary<string, string> Arguments);
