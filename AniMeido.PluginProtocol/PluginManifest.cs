using System.Text.Json;
using System.Text.Json.Serialization;

namespace AniMeido.PluginProtocol;

/// <summary>
/// 插件包根目录的第 2 版清单；声明入口、贡献项和文件摘要，不负责校验或激活插件。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>当前清单格式版本，与 Host 通信协议版本无关。</summary>
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

    /// <summary>读取 JSON 清单；只反序列化，不检查版本、路径或包文件。</summary>
    /// <returns>清单对象；JSON 根值为 null 时返回 null。</returns>
    /// <exception cref="JsonException">JSON 无效或字段类型不能转换。</exception>
    public static PluginManifest? Load(string json)
        => JsonSerializer.Deserialize<PluginManifest>(json, SerializerOptions);

    /// <summary>从文件读取清单；未找到文件时返回 null，读取或解析异常交给调用方。</summary>
    public static PluginManifest? LoadFromFile(string manifestPath)
        => File.Exists(manifestPath)
            ? Load(File.ReadAllText(manifestPath))
            : null;

    /// <summary>只将路径分隔符改为正斜线；不解析路径，也不保证路径位于插件包内。</summary>
    public static string NormalizePackagePath(string path)
        => path.Replace('\\', '/');

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>插件声明的命令、导航、设置和能力；声明本身不执行这些入口。</summary>
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

/// <summary>可分派命令的稳定标识和展示信息；Id 在所属插件内标识命令。</summary>
public sealed class PluginCommandContribution
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;
}

/// <summary>主导航入口引用的命令，不传递页面对象或类型。</summary>
public sealed class PluginNavigationContribution
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;
}

/// <summary>设置入口的稳定标识和展示信息，由宿主转发到所属插件。</summary>
public sealed class PluginSettingsContribution
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;
}

/// <summary>插件包内文件的相对路径及预期 SHA-256 摘要；实际校验由包管理流程执行。</summary>
public sealed class PluginPackageFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
