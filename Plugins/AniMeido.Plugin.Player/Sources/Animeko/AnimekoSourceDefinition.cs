using System.Text.Json.Serialization;

namespace AniMeido.Plugin.Player.Sources.Animeko;

internal sealed class AnimekoSourceDefinition
{
    public string FactoryId { get; set; } = string.Empty;

    public int Version { get; set; }

    public AnimekoArguments Arguments { get; set; } = new();
}

internal sealed class AnimekoArguments
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string IconUrl { get; set; } = string.Empty;

    public AnimekoSearchConfig SearchConfig { get; set; } = new();

    public int Tier { get; set; }
}

internal sealed class AnimekoSearchConfig
{
    public string SearchUrl { get; set; } = string.Empty;

    public bool SearchUseOnlyFirstWord { get; set; }

    public bool SearchRemoveSpecial { get; set; }

    public int SearchUseSubjectNamesCount { get; set; } = 1;

    public string RawBaseUrl { get; set; } = string.Empty;

    public int RequestInterval { get; set; }

    public string SubjectFormatId { get; set; } = string.Empty;

    public AnimekoSubjectFormatA SelectorSubjectFormatA { get; set; } = new();

    public AnimekoSubjectFormatIndexed SelectorSubjectFormatIndexed { get; set; }
        = new();

    public string ChannelFormatId { get; set; } = string.Empty;

    public AnimekoChannelFormatIndexed SelectorChannelFormatFlattened
    {
        get;
        set;
    } = new();

    public AnimekoChannelFormatNoChannel SelectorChannelFormatNoChannel
    {
        get;
        set;
    } = new();

    public string DefaultResolution { get; set; } = string.Empty;

    public string DefaultSubtitleLanguage { get; set; } = string.Empty;

    public List<string> OnlySupportsPlayers { get; set; } = [];

    public bool FilterByEpisodeSort { get; set; }

    public bool FilterBySubjectName { get; set; }

    public AnimekoMediaSelection SelectMedia { get; set; } = new();

    public AnimekoVideoMatch MatchVideo { get; set; } = new();
}

internal sealed class AnimekoSubjectFormatA
{
    public string SelectLists { get; set; } = string.Empty;

    public bool PreferShorterName { get; set; }
}

internal sealed class AnimekoSubjectFormatIndexed
{
    public string SelectNames { get; set; } = string.Empty;

    public string SelectLinks { get; set; } = string.Empty;

    public bool PreferShorterName { get; set; }
}

internal sealed class AnimekoChannelFormatIndexed
{
    public string SelectChannelNames { get; set; } = string.Empty;

    public string MatchChannelName { get; set; } = string.Empty;

    public string SelectEpisodeLists { get; set; } = string.Empty;

    public string SelectEpisodesFromList { get; set; } = string.Empty;

    public string SelectEpisodeLinksFromList { get; set; } = string.Empty;

    public string MatchEpisodeSortFromName { get; set; } = string.Empty;
}

internal sealed class AnimekoChannelFormatNoChannel
{
    public string SelectEpisodes { get; set; } = string.Empty;

    public string SelectEpisodeLinks { get; set; } = string.Empty;

    public string MatchEpisodeSortFromName { get; set; } = string.Empty;
}

internal sealed class AnimekoMediaSelection
{
    public bool DistinguishSubjectName { get; set; }

    public bool DistinguishChannelName { get; set; }
}

internal sealed class AnimekoVideoMatch
{
    public bool ScanDomMediaUrls { get; set; }

    public bool ScanInlineScriptUrls { get; set; }

    public bool EnableNestedUrl { get; set; }

    public string MatchNestedUrl { get; set; } = string.Empty;

    public string MatchVideoUrl { get; set; } = string.Empty;

    public string Cookies { get; set; } = string.Empty;

    public AnimekoVideoHeaders AddHeadersToVideo { get; set; } = new();
}

internal sealed class AnimekoVideoHeaders
{
    public string Referer { get; set; } = string.Empty;

    public string UserAgent { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement> Additional
    {
        get;
        set;
    } = new(StringComparer.OrdinalIgnoreCase);
}
