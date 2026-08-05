using AniMeido.Contracts.PersonalAnime;
using AniMeido.PluginProtocol;

namespace AniMeido.PluginHost;

internal sealed class HostedPersonalAnimeDataGateway : IPersonalAnimeDataGateway
{
    private readonly JsonPipeRpcClient _client;

    public HostedPersonalAnimeDataGateway(JsonPipeRpcClient client)
        => _client = client;

    public async Task<IReadOnlyList<PersonalAnimeSelectionItem>>
        QuerySelectionAsync(
            PersonalAnimeSelectionQuery query,
            CancellationToken cancellationToken = default)
        => await _client.InvokeAsync<PersonalAnimeSelectionItem[]>(
            nameof(QuerySelectionAsync),
            [query],
            cancellationToken);

    public Task<PersonalAnimeContextSnapshot> BuildContextAsync(
        PersonalAnimeContextRequest request,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync<PersonalAnimeContextSnapshot>(
            nameof(BuildContextAsync),
            [request],
            cancellationToken);

    public Task<PersonalAnimeChangeApplyResult> ApplyChangesAsync(
        PersonalAnimeChangeSet changeSet,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync<PersonalAnimeChangeApplyResult>(
            nameof(ApplyChangesAsync),
            [changeSet],
            cancellationToken);
}
