using AniMeido.Plugin.Player.Sources;

namespace AniMeido.Plugin.Player.Playback;

/// <summary>两个存储共用一次编号变化读取；只串行准备阶段，不覆盖查源或人工选择。</summary>
internal sealed class PlayerPublicIdBoundary(
    SourceMappingStore mappings,
    PlayerExperienceSettingsStore experience)
{
    private readonly SemaphoreSlim _preparationGate = new(1, 1);

    public Task<PlayerPreparedRoute> PrepareOpenAsync(int animeId, CancellationToken ct)
        => PrepareAsync(async () =>
        {
            var prepared = await experience.PrepareRouteAsync(animeId, ct).ConfigureAwait(false);
            await mappings.EnsurePublicIdsAsync(prepared.Changes, ct).ConfigureAwait(false);
            return new PlayerPreparedRoute(prepared.Identity, prepared.ReadResult);
        }, ct);

    public Task<PlayerLookupPreparation> PrepareLookupAsync(PlayerRouteIdentity identity, CancellationToken ct)
        => PrepareAsync(async () =>
        {
            var prepared = await experience.PrepareRouteAsync(identity, ct).ConfigureAwait(false);
            var lookup = await mappings.BeginLookupAsync(
                checked((int)prepared.Identity.CurrentAnimeId), prepared.Changes, ct).ConfigureAwait(false);
            return new PlayerLookupPreparation(new PlayerPreparedRoute(prepared.Identity, prepared.ReadResult), lookup);
        }, ct);

    public Task<PlayerExperienceReadResult> PrepareSettingsAsync(CancellationToken ct)
        => PrepareAsync(async () =>
        {
            var changes = await experience.ReadPublicIdChangesAsync(ct).ConfigureAwait(false);
            var prepared = await experience.ReadAndApplyPublicIdsAsync(changes, ct).ConfigureAwait(false);
            await mappings.EnsurePublicIdsAsync(changes, ct).ConfigureAwait(false);
            return prepared;
        }, ct);

    private async Task<T> PrepareAsync<T>(Func<Task<T>> prepare, CancellationToken ct)
    {
        await _preparationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await prepare().ConfigureAwait(false);
        }
        finally
        {
            _preparationGate.Release();
        }
    }
}

internal sealed record PlayerLookupPreparation(
    PlayerPreparedRoute Experience,
    SourceMappingLookup Mapping);

internal sealed record PlayerPreparedRoute(
    PlayerRouteIdentity Identity,
    PlayerExperienceReadResult ReadResult);
