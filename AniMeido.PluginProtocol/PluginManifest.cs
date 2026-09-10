using System.Text.Json;
using System.Text.Json.Serialization;

namespace AniMeido.PluginProtocol;

/// <summary>
/// Version 2 metadata stored at the root of an AniMeido plugin package.
/// </summary>
public sealed class PluginManifest
{
    public const int CurrentFormatVersion = 2;

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

    [JsonPropertyName("activationEvents")]
    public List<string> ActivationEvents { get; set; } = [];

    [JsonPropertyName("contributes")]
    public PluginContributions Contributions { get; set; } = new();

    [JsonPropertyName("files")]
    public List<PluginPackageFile> Files { get; set; } = [];

    public static PluginManifest? Load(string json)
        => JsonSerializer.Deserialize<PluginManifest>(json, SerializerOptions);

    public static PluginManifest? LoadFromFile(string manifestPath)
        => File.Exists(manifestPath)
            ? Load(File.ReadAllText(manifestPath))
            : null;

    public static string NormalizePackagePath(string path)
        => path.Replace('\\', '/');

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

public sealed class PluginContributions
{
    [JsonPropertyName("commands")]
    public List<PluginCommandContribution> Commands { get; set; } = [];

    [JsonPropertyName("navigation")]
    public List<PluginNavigationContribution> Navigation { get; set; } = [];

    [JsonPropertyName("settings")]
    public List<PluginSettingsContribution> Settings { get; set; } = [];

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];
}

public sealed class PluginCommandContribution
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;
}

public sealed class PluginNavigationContribution
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;
}

public sealed class PluginSettingsContribution
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;
}

public sealed class PluginPackageFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
