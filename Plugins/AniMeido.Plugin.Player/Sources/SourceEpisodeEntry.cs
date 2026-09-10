namespace AniMeido.Plugin.Player.Sources;

/// <summary>
/// Associates an episode with the provider instance that can resolve it.
/// </summary>
public sealed record SourceEpisodeEntry(
    string SourceName,
    SourceEpisode Episode)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Episode.Route)
        ? $"{Episode.Title} · {SourceName}"
        : $"{Episode.Title} · {SourceName} / {Episode.Route}";

    public string RouteDisplayText => string.IsNullOrWhiteSpace(Episode.Route)
        ? SourceName
        : $"{SourceName} / {Episode.Route}";

    public override string ToString() => DisplayText;
}
