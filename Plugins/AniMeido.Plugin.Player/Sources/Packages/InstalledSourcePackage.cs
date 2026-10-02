namespace AniMeido.Plugin.Player.Sources.Packages;

public sealed record InstalledSourcePackage(
    string Id,
    string DisplayName,
    Version Version,
    bool IsEnabled,
    string? Error = null,
    string? SourceKind = null,
    string? SubscriptionId = null,
    string? UpstreamPath = null,
    string? UpstreamRevision = null,
    bool IsOrphaned = false,
    bool IsUnmanaged = false,
    string? EntryFile = null)
{
    public bool IsValid => string.IsNullOrWhiteSpace(Error);

    public bool RequiresRestart => string.Equals(
        Path.GetFileName(EntryFile),
        "source.json",
        StringComparison.OrdinalIgnoreCase);

    public string DisplayText
        => IsValid
            ? $"{DisplayName} {Version} · {(IsEnabled ? "已启用" : "已禁用")}"
                + (string.IsNullOrWhiteSpace(UpstreamPath)
                    ? string.Empty
                    : $" · {UpstreamPath}")
                + (string.IsNullOrWhiteSpace(UpstreamRevision)
                    ? string.Empty
                    : $" @{UpstreamRevision[..Math.Min(8, UpstreamRevision.Length)]}")
                + (IsOrphaned ? " · 上游已移除" : string.Empty)
                + (IsUnmanaged ? " · 未托管" : string.Empty)
            : $"{DisplayName} · 损坏：{Error}";

    public override string ToString() => DisplayText;
}
