using System.Text.Json.Serialization;

namespace AniMeido.Plugin.Player.Sources.Rules;

/// <summary>
/// Declarative fixed-media source intended for controlled connectivity tests.
/// </summary>
public sealed class StaticSourceRule
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "static";

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("maintainer")]
    public string Maintainer { get; init; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("episodes")]
    public List<StaticSourceEpisodeRule> Episodes { get; init; } = [];
}

public sealed class StaticSourceEpisodeRule
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("route")]
    public string? Route { get; init; }

    [JsonPropertyName("mediaUrl")]
    public string MediaUrl { get; init; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}
