namespace AniMeido.PluginProtocol;

/// <summary>App 与 PluginHost 共用的协议版本、能力和激活路由标识。</summary>
/// <remarks>本文件 DTO 按默认 JSON 成员名传输。修改成员名、常量值或字段含义时须评审线协议兼容性。</remarks>
public static class PluginHostProtocol
{
    /// <summary>Host 要求握手请求中的协议版本与此版本精确一致。</summary>
    public const int Version = 5;
    public const string AnimePlaybackCapability = "animePlayback";
    public const string PersonalAnimeDataCapability = "personalAnimeData";
    public const string AnimePlaybackActivationEvent = "onAnimePlayback";
    public const string StartupFinishedActivationEvent = "onStartupFinished";
    public const string CommandActivationPrefix = "onCommand:";
    public const string SettingsActivationPrefix = "onSettings:";
}

/// <summary>App 发起的握手，声明协议版本和本次启动所用的回调管道。</summary>
/// <remarks>
/// <para>ProtocolVersion 必须与 Host 当前版本相等；CallbackPipeName 必须与 Host 启动参数中的回调管道名按 Ordinal 相等。</para>
/// <para>InstanceId 是 App 生成的实例标识，不替代回调管道名匹配。</para>
/// </remarks>
public sealed record PluginHostHandshakeRequest(
    int ProtocolVersion,
    string AppVersion,
    string InstanceId,
    string CallbackPipeName);

/// <summary>Host 完成握手后返回的协议版本和宿主程序版本。</summary>
public sealed record PluginHostHandshakeResponse(
    int ProtocolVersion,
    string HostVersion);

/// <summary>交给 Host 初始化的插件目录及清单。</summary>
public sealed record HostedPluginDescriptor(
    string Directory,
    PluginManifest Manifest);

/// <summary>跨进程命令贡献；路由使用 PluginId 和 CommandId，标题与图标仅用于展示。</summary>
public sealed record HostedCommandContribution(
    string PluginId,
    string CommandId,
    string Title,
    string Icon);

/// <summary>跨进程设置入口；路由使用 PluginId 和 SettingsId，不使用展示名称。</summary>
public sealed record HostedSettingsContribution(
    string PluginId,
    string PluginDisplayName,
    string SettingsId,
    string Title,
    string Icon);

/// <summary>单个插件加载失败的摘要，不跨进程传递异常对象。</summary>
public sealed record HostedPluginFailure(
    string PluginId,
    string Message);

/// <summary>Host 发布的插件贡献与失败快照，和窗口及调用活动状态分开传递。</summary>
public sealed record PluginHostSnapshot(
    IReadOnlyList<HostedCommandContribution> NavigationCommands,
    IReadOnlyList<HostedSettingsContribution> Settings,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<HostedPluginFailure> Failures);

/// <summary>供宿主退出协调使用的瞬时状态，不承诺后续始终空闲。</summary>
public sealed record PluginHostRuntimeState(
    bool HasVisibleWindows,
    int ActiveInvocationCount);

/// <summary>请求可选播放插件打开作品，不指定实际来源或播放器实现。</summary>
public sealed record AnimePlaybackRequest(
    int AnimeId,
    string Title,
    IReadOnlyList<string>? AlternateTitles);

/// <summary>携带传输确认序号的播放事实。</summary>
/// <remarks>Sequence 是本次 Host 队列按接纳顺序分配的确认序号；EventId 是接收端去重用的业务身份，二者不能互相替代。</remarks>
public sealed record HostedPlaybackProgressEvent(
    long Sequence,
    string EventId,
    int AnimeId,
    int EpisodeNumber,
    double PositionSeconds,
    double DurationSeconds,
    bool ReachedNaturalEnd,
    DateTimeOffset ObservedAt);

/// <summary>当前活跃播放会话的来源无关快照。</summary>
public sealed record HostedActivePlaybackContext(
    int AnimeId,
    string Title,
    int? EpisodeNumber,
    double? PositionSeconds,
    DateTimeOffset ObservedAt);
