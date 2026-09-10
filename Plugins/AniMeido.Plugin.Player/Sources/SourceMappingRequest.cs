namespace AniMeido.Plugin.Player.Sources;

internal sealed record SourceMappingRequest(
    string SourceId,
    string SourceName,
    IReadOnlyList<SourceAnimeCandidate> Candidates);
