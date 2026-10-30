namespace AniMeido.Contracts.Notifications;

/// <summary>由 App 提供的本地通知能力，不暴露平台通知或界面类型。</summary>
public interface IAppNotificationService
{
    /// <summary>当前宿主是否具备通知能力，不表示用户已允许投递。</summary>
    bool IsSupported { get; }

    /// <summary>当前是否允许投递，同时受平台可用性和系统设置影响。</summary>
    bool NotificationsEnabled { get; }

    /// <summary>按指定时间投递或安排本地通知；通知不可用或已关闭时失败。</summary>
    /// <remarks>完成只表示已交给通知实现，不保证用户已看到。取消令牌不撤销已经投递或排期的通知。</remarks>
    Task ScheduleAsync(
        AppNotificationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>尝试取消指定组和标识的通知，包括已排期与已显示项；能力不可用时无操作。</summary>
    Task CancelAsync(
        string group,
        string tag,
        CancellationToken cancellationToken = default);

    /// <summary>尝试取消整组通知，包括已排期与已显示项；能力不可用时无操作。</summary>
    Task CancelGroupAsync(
        string group,
        CancellationToken cancellationToken = default);

    /// <summary>按分类注册通知激活回调；同分类的新注册替代旧注册。</summary>
    /// <returns>调用方持有的注册租约，停止使用时必须释放；旧租约不会移除该分类的新注册。</returns>
    /// <remarks>回调令牌由宿主生命周期控制。释放租约只解除注册，不取消或等待已分派的回调。</remarks>
    IDisposable RegisterActivationHandler(
        string category,
        Func<AppNotificationActivation, CancellationToken, Task> handler);

    /// <summary>请求系统打开通知设置页，不直接更改通知权限。</summary>
    Task OpenNotificationSettingsAsync();
}

/// <summary>一条通知的投递内容和激活路由，不包含平台通知对象。</summary>
/// <param name="Category">激活回调使用的分类标识。</param>
/// <param name="Group">通知组，与 Tag 一起用于取消指定通知。</param>
/// <param name="Tag">组内通知标识。</param>
/// <param name="DeliveryTime">计划投递时间，不是用户阅读时间。</param>
public sealed record AppNotificationRequest(
    string Category,
    string Group,
    string Tag,
    DateTimeOffset DeliveryTime,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Arguments,
    IReadOnlyList<AppNotificationAction> Actions);

/// <summary>通知按钮的显示文字、稳定动作标识及附加激活参数。</summary>
public sealed record AppNotificationAction(
    string Label,
    string Action,
    IReadOnlyDictionary<string, string>? Arguments = null);

/// <summary>交给分类回调的激活信息，包含动作标识及其余通知参数。</summary>
public sealed record AppNotificationActivation(
    string Category,
    string Action,
    IReadOnlyDictionary<string, string> Arguments);
