using System.Text.Json;
using System.Text.Json.Serialization;

namespace AniMeido.App.Models;

/// <summary>
/// 插件清单文件 (plugin.json)，与插件主 DLL 同目录存放。
/// 包含插件元数据、DLL 哈希和签名，供 PluginSignatureVerifier 校验。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>插件唯一标识符，必须与 IPlugin.PluginID 一致。</summary>
    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = string.Empty;

    /// <summary>插件显示名称。</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>插件版本号。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    /// <summary>最低兼容的应用版本。</summary>
    [JsonPropertyName("minAppVersion")]
    public string MinAppVersion { get; set; } = "0.0.0";

    /// <summary>入口程序集文件名（如 "AniMeido.Plugin.Base.dll"）。</summary>
    [JsonPropertyName("entryAssembly")]
    public string EntryAssembly { get; set; } = string.Empty;

    /// <summary>入口程序集的 SHA-256 哈希值（十六进制小写）。</summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>哈希算法名称（当前仅支持 "SHA256"）。</summary>
    [JsonPropertyName("hashAlgorithm")]
    public string HashAlgorithm { get; set; } = "SHA256";

    /// <summary>
    /// 对规范化的 ManifestPayload 的 RSA 签名（Base64）。
    /// 签名时不包含本字段。
    /// </summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    /// <summary>
    /// 获取参与签名的规范化 JSON 字符串。
    /// 字段按字母序排列，无多余空白，不含 Signature 字段。
    /// </summary>
    [JsonIgnore]
    public string ManifestPayload
    {
        get
        {
            var payload = new Dictionary<string, object?>
            {
                ["displayName"] = DisplayName,
                ["entryAssembly"] = EntryAssembly,
                ["hash"] = Hash,
                ["hashAlgorithm"] = HashAlgorithm,
                ["minAppVersion"] = MinAppVersion,
                ["pluginId"] = PluginId,
                ["version"] = Version,
            };
            return JsonSerializer.Serialize(payload, PayloadOptions);
        }
    }

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>从 JSON 字符串反序列化清单。</summary>
    public static PluginManifest? Load(string json)
        => JsonSerializer.Deserialize<PluginManifest>(json, ManifestOptions);

    /// <summary>从 plugin.json 文件加载清单。</summary>
    public static PluginManifest? LoadFromFile(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return null;
        var json = File.ReadAllText(manifestPath);
        return Load(json);
    }
}
