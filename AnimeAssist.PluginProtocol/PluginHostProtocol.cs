namespace AniMeido.PluginProtocol;

public static class PluginHostProtocol
{
    public const int Version = 2;
    public const string AnimePlaybackCapability = "animePlayback";
    public const string AnimePlaybackActivationEvent = "onAnimePlayback";
    public const string StartupFinishedActivationEvent = "onStartupFinished";
    public const string CommandActivationPrefix = "onCommand:";
}

public sealed record PluginHostHandshakeRequest(
    int ProtocolVersion,
    string AppVersion,
    string InstanceId);

public sealed record PluginHostHandshakeResponse(
    int ProtocolVersion,
    string HostVersion);

public sealed record HostedPluginDescriptor(
    string Directory,
    PluginManifest Manifest);

public sealed record HostedCommandContribution(
    string PluginId,
    string CommandId,
    string Title,
    string Icon);

public sealed record HostedPluginFailure(
    string PluginId,
    string Message);

public sealed record PluginHostSnapshot(
    IReadOnlyList<HostedCommandContribution> NavigationCommands,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<HostedPluginFailure> Failures);

public sealed record PluginHostRuntimeState(
    bool HasVisibleWindows,
    int ActiveInvocationCount);

public sealed record AnimePlaybackRequest(
    int AnimeId,
    string Title,
    IReadOnlyList<string>? AlternateTitles);

public sealed record HostedPlaybackProgressEvent(
    long Sequence,
    string EventId,
    int AnimeId,
    int EpisodeNumber,
    double PositionSeconds,
    double DurationSeconds,
    bool ReachedNaturalEnd,
    DateTimeOffset ObservedAt);
