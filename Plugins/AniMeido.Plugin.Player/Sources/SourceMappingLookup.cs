namespace AniMeido.Plugin.Player.Sources;

/// <summary>一次查找固定的源映射视图；写回路由只由存储的已提交改绑更新。</summary>
internal sealed class SourceMappingLookup
{
    private readonly SourceMappingStore _store;

    public int AnimeId { get; }

    internal Dictionary<string, string> Values { get; }
    internal int CurrentAnimeId { get; set; }
    internal HashSet<string> ShadowedSources { get; set; } = new(StringComparer.Ordinal);

    internal SourceMappingLookup(SourceMappingStore store, int animeId, Dictionary<string, string> values)
    {
        _store = store;
        AnimeId = animeId;
        CurrentAnimeId = animeId;
        Values = values;
    }

    public Task<string?> GetAsync(string sourceId, CancellationToken ct)
        => _store.GetAsync(this, sourceId, ct);

    public Task SetAsync(string sourceId, string remoteId, CancellationToken ct)
        => _store.SetAsync(this, sourceId, remoteId, ct);

    public Task RemoveAsync(string sourceId, CancellationToken ct)
        => _store.RemoveAsync(this, sourceId, ct);
}
