namespace AniMeido.Plugin.Player.Playback;

/// <summary>一个准备阶段读取的编号变化；两个存储共用，不跨查找长期缓存。</summary>
internal sealed class PlayerPublicIdChanges
{
    private readonly Lazy<Task<Dictionary<int, int>?>> _migration;

    internal IReadOnlyList<PlayerPublicIds.RemapEntry> Remaps { get; }

    private PlayerPublicIdChanges(
        string? migrationPath,
        IReadOnlyList<PlayerPublicIds.RemapEntry> remaps,
        CancellationToken cancellationToken)
    {
        Remaps = remaps;
        _migration = new Lazy<Task<Dictionary<int, int>?>>(
            () => PlayerPublicIds.ReadAsync(migrationPath, cancellationToken));
    }

    internal static async Task<PlayerPublicIdChanges> ReadAsync(
        string? migrationPath, string? remapPath, CancellationToken cancellationToken)
    {
        var remaps = await PlayerPublicIds.ReadRemapsAsync(remapPath, 0, cancellationToken).ConfigureAwait(false);
        return new PlayerPublicIdChanges(migrationPath, remaps, cancellationToken);
    }

    internal int ResolveCurrentId(int animeId)
    {
        // 一个编号后来可能重新成为目标，不能把它更早的迁出记录再套到新作品上。
        for (var index = Remaps.Count - 1; index >= 0; index--)
        {
            if (Remaps[index].To == animeId)
                return animeId;
            if (Remaps[index].From != animeId)
                continue;

            var current = Remaps[index].To;
            for (var next = index + 1; next < Remaps.Count; next++)
            {
                if (Remaps[next].From == current)
                    current = Remaps[next].To;
            }
            return current;
        }
        return animeId;
    }

    internal Task<Dictionary<int, int>?> ReadMigrationAsync() => _migration.Value;
}
