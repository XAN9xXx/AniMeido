namespace AniMeido.Plugin.Base.ViewModels;

internal sealed record DesktopPersonalArchiveSummary(
    double? Rating, IReadOnlyList<string> Tags);

internal sealed class DesktopPersonalArchiveSummaryReader(
    Func<int, CancellationToken, Task<double?>> readRating,
    Func<int, CancellationToken, Task<IReadOnlyList<string>>> readTags)
{
    private int _generation;

    public async Task<DesktopPersonalArchiveSummary?> ReadAsync(
        int animeId, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !isCurrent()) return null;
        var generation = ++_generation;
        bool CanPublish() => generation == _generation
            && !cancellationToken.IsCancellationRequested && isCurrent();
        if (!CanPublish()) return null;
        var rating = await readRating(animeId, cancellationToken);
        if (!CanPublish()) return null;
        var tags = await readTags(animeId, cancellationToken);
        return CanPublish() ? new(rating, tags) : null;
    }
}
