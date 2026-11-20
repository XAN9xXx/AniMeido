using AniMeido.Contracts.PersonalAnime;
using AniMeido.PluginProtocol;
using System.Text.Json;

namespace AniMeido.App.Services;

internal sealed class PersonalAnimeCallbackRpcTarget
{
    private readonly IPersonalAnimeDataGateway _gateway;
    private readonly string _pluginId;
    private readonly bool _hasPersonalAnimeDataCapability;

    public PersonalAnimeCallbackRpcTarget(
        IPersonalAnimeDataGateway gateway,
        PluginManifest manifest)
    {
        _gateway = gateway;
        _pluginId = manifest.PluginId;
        _hasPersonalAnimeDataCapability = manifest.Contributions.Capabilities
            .Contains(
                PluginHostProtocol.PersonalAnimeDataCapability,
                StringComparer.Ordinal);
    }

    public Task<object?> DispatchAsync(
        JsonPipeRpcRequest request,
        CancellationToken cancellationToken)
        => request.Method switch
        {
            nameof(QuerySelectionAsync) => BoxAsync(QuerySelectionAsync(
                ReadArgument<PersonalAnimeSelectionQuery>(request, 0),
                cancellationToken)),
            nameof(BuildContextAsync) => BoxAsync(BuildContextAsync(
                ReadArgument<PersonalAnimeContextRequest>(request, 0),
                cancellationToken)),
            nameof(ApplyChangesAsync) => BoxAsync(ApplyChangesAsync(
                ReadArgument<PersonalAnimeChangeSet>(request, 0),
                cancellationToken)),
            _ => throw new InvalidOperationException(
                $"未知宿主回调 RPC 方法：{request.Method}"),
        };

    public Task<IReadOnlyList<PersonalAnimeSelectionItem>> QuerySelectionAsync(
        PersonalAnimeSelectionQuery query,
        CancellationToken cancellationToken = default)
    {
        EnsureCapability();
        return _gateway.QuerySelectionAsync(query, cancellationToken);
    }

    public Task<PersonalAnimeContextSnapshot> BuildContextAsync(
        PersonalAnimeContextRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureCapability();
        return _gateway.BuildContextAsync(request, cancellationToken);
    }

    public Task<PersonalAnimeChangeApplyResult> ApplyChangesAsync(
        PersonalAnimeChangeSet changeSet,
        CancellationToken cancellationToken = default)
    {
        EnsureCapability();
        if (!string.Equals(
            changeSet.SourceId,
            _pluginId,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "个人数据写入来源与会话插件 ID 不匹配。");
        }

        return _gateway.ApplyChangesAsync(changeSet, cancellationToken);
    }

    private void EnsureCapability()
    {
        if (!_hasPersonalAnimeDataCapability)
        {
            throw new InvalidOperationException(
                "当前插件会话未声明 personalAnimeData 能力。");
        }
    }

    private static T ReadArgument<T>(JsonPipeRpcRequest request, int index)
        => index < request.Arguments.Length
            ? request.Arguments[index].Deserialize<T>()
                ?? throw new InvalidOperationException("RPC 参数为空。")
            : throw new InvalidOperationException("RPC 参数数量不足。");

    private static async Task<object?> BoxAsync<T>(Task<T> task)
        => await task;
}
