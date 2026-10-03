using System.Text.Json.Serialization;

namespace AniMeido.Updates;

internal sealed class ManifestDocument
{
    [JsonRequired]
    public int Schema { get; set; }
    [JsonRequired]
    public string? Channel { get; set; }
    [JsonRequired]
    public string? Version { get; set; }
    [JsonRequired]
    public string? ReleaseDate { get; set; }
    [JsonRequired]
    public string? MinUpdatableFrom { get; set; }
    [JsonRequired]
    public string? Notes { get; set; }
    [JsonRequired]
    public PackageDocument? Package { get; set; }
}

internal sealed class PackageDocument
{
    [JsonRequired]
    public string[]? Urls { get; set; }
    [JsonRequired]
    public long Size { get; set; }
    [JsonRequired]
    public string? Sha256 { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false, WriteIndented = true)]
[JsonSerializable(typeof(ManifestDocument))]
internal partial class ManifestJsonContext : JsonSerializerContext
{
}
