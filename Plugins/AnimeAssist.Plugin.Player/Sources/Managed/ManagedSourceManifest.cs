using System.Text.Json.Serialization;

namespace AniMeido.Plugin.Player.Sources.Managed;

public sealed class ManagedSourceManifest
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("entryAssembly")]
    public string EntryAssembly { get; init; } = string.Empty;

    [JsonPropertyName("entryType")]
    public string? EntryType { get; init; }
}
