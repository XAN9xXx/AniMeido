using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Plugin.Base.ViewModels;

public sealed class PendingReviewViewModel(PendingReviewService pending, PersonalIdRemapService remap, IAnimeDataSource source)
{
    private int _loadGeneration;
    public IReadOnlyList<PendingReviewItem> Pending { get; private set; } = [];
    public IReadOnlyList<PendingReviewItem> Kept { get; private set; } = [];

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        var items = await pending.GetItemsAsync(ct).ConfigureAwait(false);
        if (generation != _loadGeneration || ct.IsCancellationRequested) return;
        Pending = items.Where(i => i.KeptAt is null).ToList();
        Kept = items.Where(i => i.KeptAt is not null).ToList();
    }

    public Task<(List<Anime> Results, int Total)> SearchAsync(string query, int offset, CancellationToken ct) =>
        source.SearchByKeywordAsync(query, offset, ct);

    public async Task<IReadOnlyList<LocalAnimeTarget>> GetTargetsAsync(int from, CancellationToken ct) =>
        (await pending.GetLocalTargetsAsync(ct).ConfigureAwait(false)).Where(t => t.AnimeId != from).ToList();

    public Task SetKeptAsync(PendingReviewItem item, bool kept, CancellationToken ct) => pending.SetKeptAsync(item.AnimeId, kept, ct);

    public async Task<bool> ApplyAsync(PendingReviewItem item, int target, bool confirmed, bool allowMerge, CancellationToken ct)
    {
        if (!confirmed) return false;
        await remap.RebindAsync(item.AnimeId, target, allowMerge, ct).ConfigureAwait(false);
        return true;
    }
}
