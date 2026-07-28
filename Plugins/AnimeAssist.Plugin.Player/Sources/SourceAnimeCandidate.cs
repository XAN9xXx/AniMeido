namespace AniMeido.Plugin.Player.Sources;

internal sealed record SourceAnimeCandidate(
    string SourceId,
    string RemoteId,
    string Title,
    IReadOnlyDictionary<string, string>? Data = null)
{
    public override string ToString() => Title;
}
