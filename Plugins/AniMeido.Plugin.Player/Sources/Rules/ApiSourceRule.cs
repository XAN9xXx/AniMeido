using System.Text.Json.Serialization;

namespace AniMeido.Plugin.Player.Sources.Rules;

/// <summary>
/// Declarative JSON API source package. HTML/XPath rules are intentionally
/// deferred until an HTML parser is selected.
/// </summary>
public sealed class ApiSourceRule
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("maintainer")]
    public string Maintainer { get; init; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("search")]
    public ApiSearchRule Search { get; init; } = new();

    [JsonPropertyName("episodes")]
    public ApiEpisodesRule Episodes { get; init; } = new();

    [JsonPropertyName("resolve")]
    public ApiResolveRule Resolve { get; init; } = new();
}

public sealed class ApiSearchRule
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("itemsPath")]
    public string ItemsPath { get; init; } = string.Empty;

    [JsonPropertyName("idPath")]
    public string IdPath { get; init; } = string.Empty;

    [JsonPropertyName("titlePath")]
    public string TitlePath { get; init; } = string.Empty;
}

public sealed class ApiEpisodesRule
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("itemsPath")]
    public string ItemsPath { get; init; } = string.Empty;

    [JsonPropertyName("idPath")]
    public string IdPath { get; init; } = string.Empty;

    [JsonPropertyName("titlePath")]
    public string TitlePath { get; init; } = string.Empty;

    [JsonPropertyName("routePath")]
    public string? RoutePath { get; init; }
}

public sealed class ApiResolveRule
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("mediaUrlPath")]
    public string MediaUrlPath { get; init; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}
