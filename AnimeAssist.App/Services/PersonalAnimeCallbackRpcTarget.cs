using AniMeido.Contracts.PersonalAnime;
using AniMeido.PluginProtocol;
using System.Text.Json;

namespace AniMeido.App.Services;

internal sealed class PersonalAnimeCallbackRpcTarget
{
    private readonly IPersonalAnimeDataGateway _gateway;

    public PersonalAnimeCallbackRpcTarget(IPersonalAnimeDataGateway gateway)
        => _gateway = gateway;

    public Task<object?> DispatchAsync(JsonPipeRpcRequest request)
        => request.Method switch
        {
            nameof(QuerySelectionAsync) => BoxAsync(QuerySelectionAsync(
                ReadArgument<PersonalAnimeSelectionQuery>(request, 0))),
            nameof(BuildContextAsync) => BoxAsync(BuildContextAsync(
                ReadArgument<PersonalAnimeContextRequest>(request, 0))),
            nameof(ApplyChangesAsync) => BoxAsync(ApplyChangesAsync(
                ReadArgument<PersonalAnimeChangeSet>(request, 0))),
            _ => throw new InvalidOperationException(
                $"未知宿主回调 RPC 方法：{request.Method}"),
        };

    public Task<IReadOnlyList<PersonalAnimeSelectionItem>> QuerySelectionAsync(
        PersonalAnimeSelectionQuery query)
        => _gateway.QuerySelectionAsync(query);

    public Task<PersonalAnimeContextSnapshot> BuildContextAsync(
        PersonalAnimeContextRequest request)
        => _gateway.BuildContextAsync(request);

    public Task<PersonalAnimeChangeApplyResult> ApplyChangesAsync(
        PersonalAnimeChangeSet changeSet)
        => _gateway.ApplyChangesAsync(changeSet);

    private static T ReadArgument<T>(JsonPipeRpcRequest request, int index)
        => index < request.Arguments.Length
            ? request.Arguments[index].Deserialize<T>()
                ?? throw new InvalidOperationException("RPC 参数为空。")
            : throw new InvalidOperationException("RPC 参数数量不足。");

    private static async Task<object?> BoxAsync<T>(Task<T> task)
        => await task;
}
