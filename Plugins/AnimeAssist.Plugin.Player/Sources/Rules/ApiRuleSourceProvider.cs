using AniMeido.Contracts.Playback;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AniMeido.Plugin.Player.Sources.Rules;

internal sealed class ApiRuleSourceProvider : IOnlineAnimeSource
{
    private readonly HttpClient _httpClient;
    private readonly ApiSourceRule _rule;

    public ApiRuleSourceProvider(HttpClient httpClient, ApiSourceRule rule)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(rule);
        _httpClient = httpClient;
        _rule = rule;
        ValidateRule(rule);
    }

    public string Id => _rule.Id;

    public string DisplayName => _rule.DisplayName;

    public async Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
        AnimePlaybackContext anime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anime);
        using var searchDocument = await RequestJsonAsync(
            Expand(_rule.Search.Url, ("query", Uri.EscapeDataString(anime.Title))),
            _rule.Headers,
            cancellationToken);
        var candidates = JsonPathReader.ReadItems(
            searchDocument.RootElement,
            _rule.Search.ItemsPath);
        var match = candidates
            .Select(item => new
            {
                Id = JsonPathReader.ReadString(item, _rule.Search.IdPath),
                Title = JsonPathReader.ReadString(item, _rule.Search.TitlePath),
            })
            .OrderByDescending(item => string.Equals(
                item.Title,
                anime.Title,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.Title.Contains(
                anime.Title,
                StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (match is null)
        {
            return [];
        }

        using var episodeDocument = await RequestJsonAsync(
            Expand(
                _rule.Episodes.Url,
                ("animeId", Uri.EscapeDataString(match.Id))),
            _rule.Headers,
            cancellationToken);
        var items = JsonPathReader.ReadItems(
            episodeDocument.RootElement,
            _rule.Episodes.ItemsPath);
        return items.Select(item =>
        {
            var episodeId = JsonPathReader.ReadString(
                item,
                _rule.Episodes.IdPath);
            var title = JsonPathReader.ReadString(
                item,
                _rule.Episodes.TitlePath);
            var route = string.IsNullOrWhiteSpace(_rule.Episodes.RoutePath)
                ? null
                : JsonPathReader.ReadString(
                    item,
                    _rule.Episodes.RoutePath);
            return new SourceEpisode(
                Id,
                episodeId,
                title,
                route,
                new Dictionary<string, string>
                {
                    ["animeId"] = match.Id,
                });
        }).ToArray();
    }

    public async Task<ResolvedMedia> ResolveAsync(
        SourceEpisode episode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var animeId = episode.Data is not null
            && episode.Data.TryGetValue("animeId", out var value)
            ? value
            : string.Empty;
        var resolveUrl = Expand(
            _rule.Resolve.Url,
            ("animeId", Uri.EscapeDataString(animeId)),
            ("episodeId", Uri.EscapeDataString(episode.EpisodeId)));
        using var document = await RequestJsonAsync(
            resolveUrl,
            _rule.Headers,
            cancellationToken);
        var mediaUrl = JsonPathReader.ReadString(
            document.RootElement,
            _rule.Resolve.MediaUrlPath);
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var mediaUri)
            && Uri.TryCreate(resolveUrl, UriKind.Absolute, out var resolveUri))
        {
            mediaUri = new Uri(resolveUri, mediaUrl);
        }

        if (mediaUri is null || mediaUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException("源规则返回了无效的媒体 URL。");
        }

        var mediaHeaders = new Dictionary<string, string>(
            _rule.Headers,
            StringComparer.OrdinalIgnoreCase);
        foreach (var header in _rule.Resolve.Headers)
        {
            mediaHeaders[header.Key] = header.Value;
        }

        return new ResolvedMedia(
            mediaUri,
            episode.Title,
            mediaHeaders);
    }

    private async Task<JsonDocument> RequestJsonAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException("源规则包含无效的请求 URL。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(
                header.Key,
                header.Value))
            {
                throw new InvalidDataException(
                    $"GET 源规则包含不支持的请求头：{header.Key}");
            }
        }

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        return await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
    }

    private static string Expand(
        string template,
        params (string Name, string Value)[] values)
    {
        var result = template;
        foreach (var value in values)
        {
            result = result.Replace(
                $"{{{value.Name}}}",
                value.Value,
                StringComparison.Ordinal);
        }

        return result;
    }

    private static void ValidateRule(ApiSourceRule rule)
    {
        if (rule.FormatVersion != 1
            || string.IsNullOrWhiteSpace(rule.Id)
            || string.IsNullOrWhiteSpace(rule.DisplayName)
            || rule.Headers is null
            || rule.Headers.Any(header =>
                string.IsNullOrWhiteSpace(header.Key)
                || header.Value is null)
            || rule.Search is null
            || string.IsNullOrWhiteSpace(rule.Search.Url)
            || string.IsNullOrWhiteSpace(rule.Search.IdPath)
            || string.IsNullOrWhiteSpace(rule.Search.TitlePath)
            || rule.Episodes is null
            || string.IsNullOrWhiteSpace(rule.Episodes.Url)
            || string.IsNullOrWhiteSpace(rule.Episodes.IdPath)
            || string.IsNullOrWhiteSpace(rule.Episodes.TitlePath)
            || rule.Resolve is null
            || string.IsNullOrWhiteSpace(rule.Resolve.Url)
            || string.IsNullOrWhiteSpace(rule.Resolve.MediaUrlPath)
            || rule.Resolve.Headers is null)
        {
            throw new InvalidDataException("源规则缺少必填字段或版本不受支持。");
        }
    }
}
