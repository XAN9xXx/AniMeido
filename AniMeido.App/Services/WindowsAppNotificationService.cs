using AniMeido.Contracts.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AniMeido.App.Services;

public sealed class WindowsAppNotificationService :
    IAppNotificationService,
    IAsyncDisposable
{
    private readonly ILogger<WindowsAppNotificationService> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, ActivationRegistration> _handlers =
        new(StringComparer.Ordinal);
    private readonly Queue<AppNotificationActivation> _pending = new();
    private DispatcherQueue? _dispatcherQueue;
    private bool _registered;
    private bool _disposed;

    public WindowsAppNotificationService(
        ILogger<WindowsAppNotificationService> logger)
        => _logger = logger;

    public bool IsSupported { get; private set; }

    public bool NotificationsEnabled =>
        IsSupported
        && AppNotificationManager.Default.Setting
            == AppNotificationSetting.Enabled;

    public Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _registered = true;
            IsSupported = true;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            IsSupported = false;
            _logger.LogWarning(
                ex,
                "Windows app notifications are unavailable.");
        }

        return Task.CompletedTask;
    }

    public Task ScheduleAsync(
        AppNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!NotificationsEnabled)
        {
            throw new InvalidOperationException("Windows 通知当前不可用或已关闭。");
        }

        var notification = BuildNotification(request);
        if (request.DeliveryTime <= DateTimeOffset.Now.AddSeconds(1))
        {
            notification.Tag = request.Tag;
            notification.Group = request.Group;
            AppNotificationManager.Default.Show(notification);
            return Task.CompletedTask;
        }

        var document = new XmlDocument();
        document.LoadXml(notification.Payload);
        var scheduled = new ScheduledToastNotification(
            document,
            request.DeliveryTime)
        {
            Tag = request.Tag,
            Group = request.Group,
        };
        var notifier = ToastNotificationManager.CreateToastNotifier();
        foreach (var existing in notifier.GetScheduledToastNotifications()
            .Where(item =>
                string.Equals(
                    item.Group,
                    request.Group,
                    StringComparison.Ordinal)
                && string.Equals(
                    item.Tag,
                    request.Tag,
                    StringComparison.Ordinal))
            .ToList())
        {
            notifier.RemoveFromSchedule(existing);
        }

        notifier.AddToSchedule(scheduled);
        return Task.CompletedTask;
    }

    public async Task CancelAsync(
        string group,
        string tag,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported)
        {
            return;
        }

        try
        {
            RemoveScheduledNotifications(group, tag);
            await AppNotificationManager.Default
                .RemoveByTagAndGroupAsync(tag, group)
                .AsTask(cancellationToken);
        }
        catch (Exception ex) when (IsNotificationInfrastructureError(ex))
        {
            _logger.LogWarning(
                ex,
                "Unable to cancel Windows notification {Group}/{Tag}.",
                group,
                tag);
        }
    }

    public async Task CancelGroupAsync(
        string group,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported)
        {
            return;
        }

        try
        {
            RemoveScheduledNotifications(group, tag: null);
            await AppNotificationManager.Default
                .RemoveByGroupAsync(group)
                .AsTask(cancellationToken);
        }
        catch (Exception ex) when (IsNotificationInfrastructureError(ex))
        {
            _logger.LogWarning(
                ex,
                "Unable to cancel Windows notification group {Group}.",
                group);
        }
    }

    internal static bool IsNotificationInfrastructureError(Exception exception)
        => exception is COMException or InvalidOperationException;

    private static void RemoveScheduledNotifications(
        string group,
        string? tag)
    {
        var notifier = ToastNotificationManager.CreateToastNotifier();
        foreach (var scheduled in notifier.GetScheduledToastNotifications()
            .Where(item =>
                string.Equals(
                    item.Group,
                    group,
                    StringComparison.Ordinal)
                && (tag is null
                    || string.Equals(
                        item.Tag,
                        tag,
                        StringComparison.Ordinal)))
            .ToList())
        {
            notifier.RemoveFromSchedule(scheduled);
        }
    }

    public IDisposable RegisterActivationHandler(
        string category,
        Func<AppNotificationActivation, CancellationToken, Task> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(handler);
        var registration = new ActivationRegistration(
            this,
            category,
            handler);
        List<AppNotificationActivation> pending;
        lock (_sync)
        {
            _handlers[category] = registration;
            pending = _pending
                .Where(item => string.Equals(
                    item.Category,
                    category,
                    StringComparison.Ordinal))
                .ToList();
            if (pending.Count > 0)
            {
                var retained = _pending.Where(item => !string.Equals(
                    item.Category,
                    category,
                    StringComparison.Ordinal)).ToList();
                _pending.Clear();
                foreach (var item in retained)
                {
                    _pending.Enqueue(item);
                }
            }
        }

        foreach (var activation in pending)
        {
            Dispatch(registration.Handler, activation);
        }

        return registration;
    }

    public async Task OpenNotificationSettingsAsync()
        => await Windows.System.Launcher.LaunchUriAsync(
            new Uri("ms-settings:notifications"));

    private static AppNotification BuildNotification(
        AppNotificationRequest request)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("category", request.Category)
            .AddArgument("action", "open")
            .AddArgument("group", request.Group)
            .AddArgument("tag", request.Tag)
            .AddText(request.Title)
            .AddText(request.Body);
        foreach (var argument in request.Arguments)
        {
            builder.AddArgument(argument.Key, argument.Value);
        }

        foreach (var action in request.Actions)
        {
            var button = new AppNotificationButton(action.Label)
                .AddArgument("category", request.Category)
                .AddArgument("action", action.Action)
                .AddArgument("group", request.Group)
                .AddArgument("tag", request.Tag);
            foreach (var argument in request.Arguments)
            {
                button.AddArgument(argument.Key, argument.Value);
            }
            if (action.Arguments is not null)
            {
                foreach (var argument in action.Arguments)
                {
                    button.AddArgument(argument.Key, argument.Value);
                }
            }

            builder.AddButton(button);
        }

        return builder.BuildNotification();
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        var values = ParseArguments(args.Argument);
        if (!values.Remove("category", out var category)
            || string.IsNullOrWhiteSpace(category))
        {
            _logger.LogWarning(
                "Ignored notification activation without a category.");
            return;
        }

        values.Remove("action", out var action);
        var activation = new AppNotificationActivation(
            category,
            string.IsNullOrWhiteSpace(action) ? "open" : action,
            values);
        ActivationRegistration? handler;
        lock (_sync)
        {
            if (!_handlers.TryGetValue(category, out handler))
            {
                _pending.Enqueue(activation);
                while (_pending.Count > 32)
                {
                    _pending.Dequeue();
                }
                return;
            }
        }

        Dispatch(handler.Handler, activation);
    }

    private void Dispatch(
        Func<AppNotificationActivation, CancellationToken, Task> handler,
        AppNotificationActivation activation)
    {
        async void Invoke()
        {
            try
            {
                await handler(activation, CancellationToken.None);
            }
#pragma warning disable CA1031 // Notification actions cannot crash the App.
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Notification action {Action} failed.",
                    activation.Action);
            }
#pragma warning restore CA1031
        }

        if (_dispatcherQueue?.HasThreadAccess == true)
        {
            Invoke();
        }
        else if (_dispatcherQueue?.TryEnqueue(Invoke) != true)
        {
            lock (_sync)
            {
                _pending.Enqueue(activation);
            }
        }
    }

    private static Dictionary<string, string> ParseArguments(string arguments)
    {
        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in arguments.Split(
            '&',
            StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(
                separator >= 0 ? pair[..separator] : pair);
            var value = Uri.UnescapeDataString(
                separator >= 0 ? pair[(separator + 1)..] : string.Empty);
            values[key] = value;
        }

        return values;
    }

    private void Remove(ActivationRegistration registration)
    {
        lock (_sync)
        {
            if (_handlers.TryGetValue(
                    registration.Category,
                    out var current)
                && ReferenceEquals(current, registration))
            {
                _handlers.Remove(registration.Category);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_registered)
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked -= OnNotificationInvoked;
            manager.Unregister();
            _registered = false;
        }

        lock (_sync)
        {
            _handlers.Clear();
            _pending.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private sealed class ActivationRegistration : IDisposable
    {
        private WindowsAppNotificationService? _owner;

        public ActivationRegistration(
            WindowsAppNotificationService owner,
            string category,
            Func<AppNotificationActivation, CancellationToken, Task> handler)
        {
            _owner = owner;
            Category = category;
            Handler = handler;
        }

        public string Category { get; }

        public Func<AppNotificationActivation, CancellationToken, Task>
            Handler { get; }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Remove(this);
    }
}
