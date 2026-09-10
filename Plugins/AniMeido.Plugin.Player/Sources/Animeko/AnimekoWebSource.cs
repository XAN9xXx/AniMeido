using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources.Web;
using System.Text.RegularExpressions;

namespace AniMeido.Plugin.Player.Sources.Animeko;

internal sealed partial class AnimekoWebSource :
    IMappableOnlineAnimeSource,
    ISourceTier
{
    private readonly HttpClient _httpClient;
    private readonly WebMediaResolver _webResolver;
    private readonly AnimekoSourceDefinition _definition;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastRequestUtc;

    public AnimekoWebSource(
        string id,
        HttpClient httpClient,
        WebMediaResolver webResolver,
        AnimekoSourceDefinition definition)
    {
        Id = id;
        _httpClient = httpClient;
        _webResolver = webResolver;
        _definition = definition;
        ValidateDefinition(definition);
    }

    public string Id { get; }

    public string DisplayName => _definition.Arguments.Name;

    public int Tier => _definition.Arguments.Tier;

    public TimeSpan SearchTimeout => TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<SourceAnimeCandidate>> SearchAsync(
        AnimePlaybackContext anime,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, SourceAnimeCandidate>(
            StringComparer.Ordinal);
        var config = _definition.Arguments.SearchConfig;
        foreach (var title in TitleMatcher.GetSearchTitles(anime)
            .Take(Math.Max(1, config.SearchUseSubjectNamesCount)))
        {
            var keyword = NormalizeKeyword(title, config);
            var url = config.SearchUrl.Replace(
                "{keyword}",
                Uri.EscapeDataString(keyword),
                StringComparison.Ordinal);
            var html = await GetStringAsync(url, cancellationToken);
            var document = await new HtmlParser().ParseDocumentAsync(
                html,
                cancellationToken);
            foreach (var candidate in ReadCandidates(document, new Uri(url)))
            {
                candidates.TryAdd(candidate.RemoteId, candidate);
            }
        }

        return candidates.Values.ToArray();
    }

    public async Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
        SourceAnimeCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.Data is null
            || !candidate.Data.TryGetValue("url", out var value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var detailUri))
        {
            throw new InvalidDataException("ani-subs 候选缺少详情页 URL。");
        }

        var html = await GetStringAsync(
            detailUri.AbsoluteUri,
            cancellationToken);
        var document = await new HtmlParser().ParseDocumentAsync(
            html,
            cancellationToken);
        var config = _definition.Arguments.SearchConfig;
        return string.Equals(
            config.ChannelFormatId,
            "no-channel",
            StringComparison.Ordinal)
            ? ReadNoChannelEpisodes(document, detailUri)
            : ReadGroupedEpisodes(document, detailUri);
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
        if (episode.Data is null
            || !episode.Data.TryGetValue("url", out var value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var pageUri))
        {
            throw new InvalidDataException("ani-subs 剧集缺少播放页 URL。");
        }

        var config = _definition.Arguments.SearchConfig;
        var headers = BuildHeaders(config.MatchVideo.AddHeadersToVideo);
        var resolved = await _webResolver.ResolveAsync(
            new WebResolutionRequest(
                Id,
                pageUri,
                config.MatchVideo.MatchVideoUrl,
                config.MatchVideo.EnableNestedUrl,
                config.MatchVideo.MatchNestedUrl,
                config.MatchVideo.ScanDomMediaUrls,
                config.MatchVideo.ScanInlineScriptUrls,
                headers,
                config.MatchVideo.Cookies),
            cancellationToken);
        return new ResolvedMedia(
            resolved.Uri,
            episode.Title,
            resolved.Headers);
    }

    private IEnumerable<SourceAnimeCandidate> ReadCandidates(
        IDocument document,
        Uri baseUri)
    {
        var config = _definition.Arguments.SearchConfig;
        if (string.Equals(config.SubjectFormatId, "indexed", StringComparison.Ordinal))
        {
            var names = document.QuerySelectorAll(
                config.SelectorSubjectFormatIndexed.SelectNames);
            var links = document.QuerySelectorAll(
                config.SelectorSubjectFormatIndexed.SelectLinks);
            for (var index = 0; index < Math.Min(names.Length, links.Length); index++)
            {
                var href = links[index].GetAttribute("href");
                if (TryCreateAbsolute(baseUri, href, out var uri))
                {
                    yield return CreateCandidate(names[index].TextContent, uri);
                }
            }

            yield break;
        }

        foreach (var element in document.QuerySelectorAll(
            config.SelectorSubjectFormatA.SelectLists))
        {
            var link = element.LocalName == "a"
                ? element
                : element.QuerySelector("a[href]");
            var href = link?.GetAttribute("href");
            if (link is not null && TryCreateAbsolute(baseUri, href, out var uri))
            {
                var title = link.GetAttribute("title");
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = link.TextContent;
                }

                yield return CreateCandidate(title, uri);
            }
        }
    }

    private IReadOnlyList<SourceEpisode> ReadGroupedEpisodes(
        IDocument document,
        Uri baseUri)
    {
        var format = _definition.Arguments.SearchConfig
            .SelectorChannelFormatFlattened;
        var channelNames = document.QuerySelectorAll(format.SelectChannelNames);
        var episodeLists = document.QuerySelectorAll(format.SelectEpisodeLists);
        var episodes = new List<SourceEpisode>();
        for (var index = 0; index < episodeLists.Length; index++)
        {
            string route;
            if (index < channelNames.Length)
            {
                if (!TryExtractNamedValue(
                        channelNames[index].TextContent,
                        format.MatchChannelName,
                        "ch",
                        out route))
                {
                    continue;
                }
            }
            else
            {
                route = $"线路 {index + 1}";
            }

            foreach (var element in episodeLists[index].QuerySelectorAll(
                format.SelectEpisodesFromList))
            {
                AddEpisode(
                    episodes,
                    element,
                    format.SelectEpisodeLinksFromList,
                    route,
                    baseUri,
                    format.MatchEpisodeSortFromName);
            }
        }

        return episodes;
    }

    private IReadOnlyList<SourceEpisode> ReadNoChannelEpisodes(
        IDocument document,
        Uri baseUri)
    {
        var format = _definition.Arguments.SearchConfig
            .SelectorChannelFormatNoChannel;
        var episodes = new List<SourceEpisode>();
        foreach (var element in document.QuerySelectorAll(format.SelectEpisodes))
        {
            AddEpisode(
                episodes,
                element,
                format.SelectEpisodeLinks,
                null,
                baseUri,
                format.MatchEpisodeSortFromName);
        }

        return episodes;
    }

    private void AddEpisode(
        ICollection<SourceEpisode> episodes,
        IElement element,
        string linkSelector,
        string? route,
        Uri baseUri,
        string episodePattern)
    {
        var title = element.TextContent.Trim();
        if (_definition.Arguments.SearchConfig.FilterByEpisodeSort
            && !string.IsNullOrWhiteSpace(episodePattern)
            && !Regex.IsMatch(title, episodePattern))
        {
            return;
        }

        var link = string.IsNullOrWhiteSpace(linkSelector)
            ? element
            : element.QuerySelector(linkSelector);
        var href = link?.GetAttribute("href");
        if (link is null || !TryCreateAbsolute(baseUri, href, out var uri))
        {
            return;
        }

        episodes.Add(new SourceEpisode(
            Id,
            uri.AbsoluteUri,
            title,
            route,
            new Dictionary<string, string>
            {
                ["url"] = uri.AbsoluteUri,
            }));
    }

    private SourceAnimeCandidate CreateCandidate(string title, Uri uri)
        => new(
            Id,
            uri.AbsoluteUri,
            title.Trim(),
            new Dictionary<string, string>
            {
                ["url"] = uri.AbsoluteUri,
            });

    private async Task<string> GetStringAsync(
        string url,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = TimeSpan.FromMilliseconds(
                Math.Max(0, _definition.Arguments.SearchConfig.RequestInterval))
                - (DateTimeOffset.UtcNow - _lastRequestUtc);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            try
            {
                using var response = await _httpClient.GetAsync(
                    url,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                _lastRequestUtc = DateTimeOffset.UtcNow;
                return await response.Content.ReadAsStringAsync(
                    cancellationToken);
            }
            catch (HttpRequestException)
            {
                var html = await _webResolver.LoadPageHtmlAsync(
                    new Uri(url),
                    interactive: true,
                    cancellationToken);
                _lastRequestUtc = DateTimeOffset.UtcNow;
                return html;
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    internal static void ValidateDefinition(AnimekoSourceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var config = definition.Arguments.SearchConfig;
        if (!string.Equals(
                definition.FactoryId,
                "web-selector",
                StringComparison.Ordinal)
            || definition.Version != 2
            || string.IsNullOrWhiteSpace(definition.Arguments.Name)
            || string.IsNullOrWhiteSpace(config.SearchUrl)
            || !Uri.TryCreate(
                config.SearchUrl.Replace(
                    "{keyword}",
                    "test",
                    StringComparison.Ordinal),
                UriKind.Absolute,
                out var searchUri)
            || searchUri.Scheme is not ("http" or "https")
            || config.RequestInterval is < 0 or > 60_000
            || !HasSubjectSelectors(config)
            || !HasEpisodeSelectors(config)
            || string.IsNullOrWhiteSpace(config.MatchVideo.MatchVideoUrl)
            || config.OnlySupportsPlayers.Count > 0
                && !config.OnlySupportsPlayers.Contains(
                    "mpv",
                    StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "ani-subs Web Selector 配置无效或不支持 libmpv。");
        }

        _ = new Regex(
            config.MatchVideo.MatchVideoUrl,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        if (config.MatchVideo.EnableNestedUrl
            && !string.IsNullOrWhiteSpace(config.MatchVideo.MatchNestedUrl))
        {
            _ = new Regex(
                config.MatchVideo.MatchNestedUrl,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        }
    }

    private static bool HasSubjectSelectors(AnimekoSearchConfig config)
        => config.SubjectFormatId switch
        {
            "a" => !string.IsNullOrWhiteSpace(
                config.SelectorSubjectFormatA.SelectLists),
            "indexed" => !string.IsNullOrWhiteSpace(
                    config.SelectorSubjectFormatIndexed.SelectNames)
                && !string.IsNullOrWhiteSpace(
                    config.SelectorSubjectFormatIndexed.SelectLinks),
            _ => false,
        };

    private static bool HasEpisodeSelectors(AnimekoSearchConfig config)
        => config.ChannelFormatId switch
        {
            "no-channel" => !string.IsNullOrWhiteSpace(
                config.SelectorChannelFormatNoChannel.SelectEpisodes),
            "index-grouped" => !string.IsNullOrWhiteSpace(
                    config.SelectorChannelFormatFlattened.SelectEpisodeLists)
                && !string.IsNullOrWhiteSpace(
                    config.SelectorChannelFormatFlattened
                        .SelectEpisodesFromList),
            _ => false,
        };

    private static string NormalizeKeyword(
        string title,
        AnimekoSearchConfig config)
    {
        var value = title.Trim();
        if (config.SearchUseOnlyFirstWord)
        {
            value = value.Split(
                [' ', '　'],
                StringSplitOptions.RemoveEmptyEntries)[0];
        }

        return config.SearchRemoveSpecial
            ? SpecialCharacterRegex().Replace(value, string.Empty)
            : value;
    }

    private static Dictionary<string, string> BuildHeaders(
        AnimekoVideoHeaders source)
    {
        var headers = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(source.Referer))
        {
            headers["Referer"] = source.Referer;
        }

        if (!string.IsNullOrWhiteSpace(source.UserAgent))
        {
            headers["User-Agent"] = source.UserAgent;
        }

        foreach (var item in source.Additional)
        {
            if (item.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                headers[item.Key] = item.Value.GetString() ?? string.Empty;
            }
        }

        return headers;
    }

    private static bool TryExtractNamedValue(
        string value,
        string pattern,
        string groupName,
        out string result)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            result = value.Trim();
            return true;
        }

        var match = Regex.Match(value, pattern);
        result = match.Success && match.Groups[groupName].Success
            ? match.Groups[groupName].Value.Trim()
            : value.Trim();
        return match.Success;
    }

    private static bool TryCreateAbsolute(
        Uri baseUri,
        string? value,
        out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && uri.Scheme is "http" or "https")
        {
            return true;
        }

        return Uri.TryCreate(baseUri, value, out uri!)
            && uri.Scheme is "http" or "https";
    }

    [GeneratedRegex(
        @"[\p{P}\p{S}]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SpecialCharacterRegex();
}
