namespace AniMeido.Plugin.Base.Models;

/// <summary>Process-local browsing data. Never retains a view or its cancellation lifetime.</summary>
public sealed class RecommendationBrowseState
{
    public RecommendationSnapshot? Snapshot { get; set; }
    public IReadOnlyList<RecommendationFeatureProfile> Profile { get; set; } = [];
    public int? SelectedAnimeId { get; set; }
    public string Section { get; set; } = "recommendations";
    public double VerticalOffset { get; set; }
    public bool HasPendingPreferences { get; set; }
    public HashSet<int> SkippedIds { get; } = [];
    public HashSet<int> RemovedIds { get; } = [];
    public Dictionary<int, string> TrackingLabels { get; } = [];

    public IReadOnlyList<RecommendationItem> VisibleItems => Snapshot?.Items
        .Where(item => !SkippedIds.Contains(item.Anime.ID) && !RemovedIds.Contains(item.Anime.ID))
        .ToArray() ?? [];

    public void StartRound(RecommendationSnapshot snapshot)
    {
        Snapshot = snapshot;
        SkippedIds.Clear();
        RemovedIds.Clear();
        HasPendingPreferences = false;
        VerticalOffset = 0;
    }

    public static int? SelectAfterRemoval(IReadOnlyList<RecommendationItem> items, int oldIndex)
        => items.Count == 0 ? null : items[Math.Clamp(oldIndex, 0, items.Count - 1)].Anime.ID;
}
