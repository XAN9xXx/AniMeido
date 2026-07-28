using AniMeido.PluginProtocol;
using System.Text.Json;

namespace AniMeido.PluginHost;

public sealed class PluginHostRpcTarget
{
    private readonly PluginRuntimeCatalog _catalog;
    private int _shutdownRequested;

    internal PluginHostRpcTarget(PluginRuntimeCatalog catalog)
        => _catalog = catalog;

    internal bool ShutdownRequested =>
        Volatile.Read(ref _shutdownRequested) != 0;

    public Task<object?> DispatchAsync(JsonPipeRpcRequest request)
        => request.Method switch
        {
            nameof(HandshakeAsync) => BoxAsync(HandshakeAsync(
                ReadArgument<PluginHostHandshakeRequest>(request, 0))),
            nameof(InitializeAsync) => BoxAsync(InitializeAsync(
                ReadArgument<HostedPluginDescriptor[]>(request, 0))),
            nameof(InvokeCommandAsync) => BoxAsync(InvokeCommandAsync(
                ReadArgument<string>(request, 0),
                ReadArgument<string>(request, 1))),
            nameof(LaunchAnimePlaybackAsync) => BoxAsync(
                LaunchAnimePlaybackAsync(
                    ReadArgument<AnimePlaybackRequest>(request, 0))),
            nameof(GetRuntimeStateAsync) => BoxAsync(GetRuntimeStateAsync()),
            nameof(GetPlaybackProgressEventsAsync) => BoxAsync(
                GetPlaybackProgressEventsAsync()),
            nameof(AcknowledgePlaybackProgressEventsAsync) => BoxAsync(
                AcknowledgePlaybackProgressEventsAsync(
                    ReadArgument<long>(request, 0))),
            nameof(ShutdownAsync) => BoxAsync(ShutdownAsync()),
            _ => throw new InvalidOperationException(
                $"未知 PluginHost RPC 方法：{request.Method}"),
        };

    private static T ReadArgument<T>(
        JsonPipeRpcRequest request,
        int index)
        => index < request.Arguments.Length
            ? request.Arguments[index].Deserialize<T>()
                ?? throw new InvalidOperationException("RPC 参数为空。")
            : throw new InvalidOperationException("RPC 参数数量不足。");

    private static async Task<object?> BoxAsync<T>(Task<T> task)
        => await task;

    private static async Task<object?> BoxAsync(Task task)
    {
        await task;
        return null;
    }

    public Task<PluginHostHandshakeResponse> HandshakeAsync(
        PluginHostHandshakeRequest request)
    {
        if (request.ProtocolVersion != PluginHostProtocol.Version)
        {
            throw new InvalidOperationException(
                $"IPC 协议版本不兼容：App={request.ProtocolVersion}, Host={PluginHostProtocol.Version}。");
        }

        var version = typeof(PluginHostRpcTarget).Assembly
            .GetName()
            .Version?
            .ToString(3) ?? "0.0.0";
        return Task.FromResult(new PluginHostHandshakeResponse(
            PluginHostProtocol.Version,
            version));
    }

    public Task<PluginHostSnapshot> InitializeAsync(
        HostedPluginDescriptor[] plugins)
        => _catalog.InitializeAsync(plugins);

    public Task InvokeCommandAsync(
        string pluginId,
        string commandId)
        => _catalog.InvokeCommandAsync(pluginId, commandId);

    public Task LaunchAnimePlaybackAsync(AnimePlaybackRequest request)
        => _catalog.LaunchAnimePlaybackAsync(request);

    public Task<PluginHostRuntimeState> GetRuntimeStateAsync()
        => Task.FromResult(_catalog.GetRuntimeState());

    public Task<HostedPlaybackProgressEvent[]> GetPlaybackProgressEventsAsync()
        => Task.FromResult(_catalog.GetPlaybackProgressEvents());

    public Task AcknowledgePlaybackProgressEventsAsync(long sequence)
    {
        _catalog.AcknowledgePlaybackProgressEvents(sequence);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync()
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        return Task.CompletedTask;
    }
}
