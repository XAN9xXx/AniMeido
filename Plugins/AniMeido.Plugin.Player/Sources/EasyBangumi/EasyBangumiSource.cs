using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources.Web;
using Jint;
using System.Text.Json;

namespace AniMeido.Plugin.Player.Sources.EasyBangumi;

internal sealed class EasyBangumiSource : IMappableOnlineAnimeSource
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly HttpClient _httpClient;
    private readonly WebMediaResolver _webResolver;
    private readonly EasyPreferenceStore _preferenceStore;
    private readonly string _script;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public EasyBangumiSource(
        string id,
        string displayName,
        string script,
        HttpClient httpClient,
        WebMediaResolver webResolver,
        EasyPreferenceStore preferenceStore)
    {
        Id = id;
        DisplayName = displayName;
        _script = script;
        _httpClient = httpClient;
        _webResolver = webResolver;
        _preferenceStore = preferenceStore;
        EasyScriptCompatibility.Validate(script);
    }

    public string Id { get; }

    public string DisplayName { get; }

    public TimeSpan SearchTimeout => TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<SourceAnimeCandidate>> SearchAsync(
        AnimePlaybackContext anime,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, SourceAnimeCandidate>(
            StringComparer.Ordinal);
        foreach (var title in TitleMatcher.GetSearchTitles(anime))
        {
            var invocation = await InvokeAsync(
                "__animeido_search",
                [title],
                cancellationToken);
            var items = JsonSerializer.Deserialize<List<EasySearchResult>>(
                invocation.Value,
                SerializerOptions)
                ?? [];
            foreach (var item in items.Where(item =>
                !string.IsNullOrWhiteSpace(item.Id)
                && !string.IsNullOrWhiteSpace(item.Title)))
            {
                var summaryJson = JsonSerializer.Serialize(new
                {
                    id = item.Id,
                    title = item.Title,
                    url = item.Url,
                    source = Id,
                    cover = item.Cover,
                });
                results.TryAdd(item.Id, new SourceAnimeCandidate(
                    Id,
                    item.Id,
                    item.Title,
                    new Dictionary<string, string>
                    {
                        ["summary"] = summaryJson,
                    }));
            }
        }

        return results.Values.ToArray();
    }

    public async Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
        SourceAnimeCandidate candidate,
        CancellationToken cancellationToken)
    {
        var summary = GetRequired(candidate.Data, "summary");
        var invocation = await InvokeAsync(
            "__animeido_episodes",
            [summary],
            cancellationToken);
        var items = JsonSerializer.Deserialize<List<EasyEpisodeResult>>(
            invocation.Value,
            SerializerOptions)
            ?? [];
        return items.Select(item => new SourceEpisode(
            Id,
            $"{item.PlayLineId}:{item.EpisodeId}",
            item.Title,
            item.Route,
            new Dictionary<string, string>
            {
                ["summary"] = summary,
                ["playLineId"] = item.PlayLineId,
                ["route"] = item.Route,
                ["episodeId"] = item.EpisodeId,
            })).ToArray();
    }

    public async Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
        AnimePlaybackContext anime,
        CancellationToken cancellationToken)
    {
        var candidates = TitleMatcher.Rank(
            anime,
            await SearchAsync(anime, cancellationToken));
        return candidates.Count == 0
            ? []
            : await GetEpisodesAsync(candidates[0], cancellationToken);
    }

    public async Task<ResolvedMedia> ResolveAsync(
        SourceEpisode episode,
        CancellationToken cancellationToken)
    {
        var summary = GetRequired(episode.Data, "summary");
        var playLineId = GetRequired(episode.Data, "playLineId");
        var route = GetRequired(episode.Data, "route");
        var episodeId = GetRequired(episode.Data, "episodeId");
        var invocation = await InvokeAsync(
            "__animeido_resolve",
            [summary, playLineId, route, episodeId, episode.Title],
            cancellationToken);
        var result = JsonSerializer.Deserialize<EasyResolveResult>(
            invocation.Value,
            SerializerOptions)
            ?? throw new InvalidDataException("EasyBangumi 返回了空播放信息。");
        if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException("EasyBangumi 返回了无效播放地址。");
        }

        return new ResolvedMedia(
            uri,
            episode.Title,
            MergeResolvedHeaders(
                result.Headers,
                invocation.ResolvedHeaders));
    }

    internal static IReadOnlyDictionary<string, string> MergeResolvedHeaders(
        IReadOnlyDictionary<string, string>? scriptHeaders,
        IReadOnlyDictionary<string, string>? resolverHeaders)
        => HeaderNormalizer.Merge(scriptHeaders, resolverHeaders);

    private async Task<EasyInvocationResult> InvokeAsync(
        string function,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            var preferences = await _preferenceStore.ReadAsync(
                Id,
                cancellationToken);
            return await Task.Run(() =>
            {
                var bridge = new EasyHostBridge(
                    _httpClient,
                    _webResolver,
                    Id,
                    preferences,
                    cancellationToken);
                var engine = new Engine(options => options
                    .CancellationToken(cancellationToken)
                    .LimitMemory(32_000_000)
                    .MaxStatements(250_000)
                    .TimeoutInterval(TimeSpan.FromSeconds(110)));
                engine.SetValue("__sourceId", Id);
                engine.SetValue("__host", bridge);
                engine.SetValue("__xpath", new EasyXPathFacade());
                engine.Execute(EasyBangumiPrelude.Script);
                engine.Execute(_script);
                var value = engine.Invoke(function, arguments).AsString();
                return new EasyInvocationResult(
                    value,
                    bridge.LastResolvedHeaders);
            }, cancellationToken);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private static string GetRequired(
        IReadOnlyDictionary<string, string>? values,
        string key)
        => values is not null
            && values.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidDataException(
                    $"EasyBangumi 数据缺少 {key}。");

    private sealed class EasySearchResult
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string Cover { get; set; } = string.Empty;
    }

    private sealed class EasyEpisodeResult
    {
        public string PlayLineId { get; set; } = string.Empty;

        public string Route { get; set; } = string.Empty;

        public string EpisodeId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;
    }

    private sealed class EasyResolveResult
    {
        public string Url { get; set; } = string.Empty;

        public Dictionary<string, string> Headers { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record EasyInvocationResult(
        string Value,
        IReadOnlyDictionary<string, string>? ResolvedHeaders);
}
