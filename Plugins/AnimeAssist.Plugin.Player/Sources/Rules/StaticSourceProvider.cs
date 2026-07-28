using AniMeido.Contracts.Playback;

namespace AniMeido.Plugin.Player.Sources.Rules;

internal sealed class StaticSourceProvider : IOnlineAnimeSource
{
    private readonly StaticSourceRule _rule;
    private readonly IReadOnlyDictionary<string, StaticSourceEpisodeRule>
        _episodes;

    public StaticSourceProvider(StaticSourceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ValidateRule(rule);
        _rule = rule;
        _episodes = rule.Episodes.ToDictionary(
            episode => episode.Id,
            StringComparer.Ordinal);
    }

    public string Id => _rule.Id;

    public string DisplayName => _rule.DisplayName;

    public Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
        AnimePlaybackContext anime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anime);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SourceEpisode> episodes = _rule.Episodes
            .Select(episode => new SourceEpisode(
                Id,
                episode.Id,
                episode.Title,
                episode.Route))
            .ToArray();
        return Task.FromResult(episodes);
    }

    public Task<ResolvedMedia> ResolveAsync(
        SourceEpisode episode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(episode.SourceId, Id, StringComparison.Ordinal)
            || !_episodes.TryGetValue(episode.EpisodeId, out var configured))
        {
            throw new InvalidOperationException("静态源无法解析未知剧集。");
        }

        var mediaHeaders = new Dictionary<string, string>(
            _rule.Headers,
            StringComparer.OrdinalIgnoreCase);
        foreach (var header in configured.Headers)
        {
            mediaHeaders[header.Key] = header.Value;
        }

        return Task.FromResult(new ResolvedMedia(
            new Uri(configured.MediaUrl),
            configured.Title,
            mediaHeaders));
    }

    private static void ValidateRule(StaticSourceRule rule)
    {
        if (rule.FormatVersion != 1
            || !string.Equals(rule.Kind, "static", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(rule.Id)
            || string.IsNullOrWhiteSpace(rule.DisplayName)
            || rule.Headers is null
            || rule.Episodes is null
            || rule.Episodes.Count == 0
            || rule.Episodes.Any(episode =>
                string.IsNullOrWhiteSpace(episode.Id)
                || string.IsNullOrWhiteSpace(episode.Title)
                || episode.Headers is null
                || !Uri.TryCreate(
                    episode.MediaUrl,
                    UriKind.Absolute,
                    out var mediaUri)
                || mediaUri.Scheme is not ("http" or "https"))
            || rule.Episodes.Select(episode => episode.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != rule.Episodes.Count)
        {
            throw new InvalidDataException("静态源规则缺少必填字段或格式无效。");
        }
    }
}
