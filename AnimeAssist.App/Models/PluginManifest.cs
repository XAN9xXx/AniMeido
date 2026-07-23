using System.Text.Json;
using System.Text.Json.Serialization;

namespace AniMeido.App.Models;

/// <summary>
/// Metadata stored at the root of an AniMeido plugin package.
/// </summary>
public sealed class PluginManifest
{
    public const int CurrentFormatVersion = 1;

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("minAppVersion")]
    public string MinAppVersion { get; set; } = "0.0.0";

    [JsonPropertyName("entryAssembly")]
    public string EntryAssembly { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<PluginPackageFile> Files { get; set; } = [];

    public static PluginManifest? Load(string json)
        => JsonSerializer.Deserialize<PluginManifest>(json, SerializerOptions);

    public static PluginManifest? LoadFromFile(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        return Load(File.ReadAllText(manifestPath));
    }

    public static string NormalizePackagePath(string path)
        => path.Replace('\\', '/');

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

}

public sealed class PluginPackageFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
