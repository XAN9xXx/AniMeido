using System.Text.Json.Serialization;

namespace AniMeido.App.Models;

internal sealed class PluginRegistry
{
    public const int CurrentFormatVersion = 1;

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    [JsonPropertyName("plugins")]
    public List<InstalledPluginState> Plugins { get; set; } = [];
}

internal sealed class InstalledPluginState
{
    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; set; } = string.Empty;

    [JsonPropertyName("previousVersion")]
    public string? PreviousVersion { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("pendingUninstall")]
    public bool PendingUninstall { get; set; }

    [JsonPropertyName("installedAtUtc")]
    public DateTimeOffset InstalledAtUtc { get; set; }

    [JsonPropertyName("sourceFileName")]
    public string SourceFileName { get; set; } = string.Empty;

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }
}

public sealed record InstalledPluginInfo(
    string PluginId,
    string DisplayName,
    string CurrentVersion,
    string? PreviousVersion,
    bool Enabled,
    bool PendingUninstall,
    DateTimeOffset InstalledAtUtc,
    string SourceFileName,
    string? LastError)
{
    public string StatusText
        => PendingUninstall
            ? "等待重启后卸载"
            : Enabled
                ? "已启用"
                : "已禁用";

    public bool CanRollback => !string.IsNullOrWhiteSpace(PreviousVersion);

    public bool CanEnable => !Enabled && !PendingUninstall;

    public bool CanDisable => Enabled && !PendingUninstall;

    public bool CanUninstall => !PendingUninstall;

    public string SourceText => string.IsNullOrWhiteSpace(SourceFileName)
        ? "来源：未知"
        : $"来源：{SourceFileName}";
}

public sealed record PluginInstallResult(
    string PluginId,
    string DisplayName,
    string Version,
    bool IsUpgrade,
    bool RestartRequired);
