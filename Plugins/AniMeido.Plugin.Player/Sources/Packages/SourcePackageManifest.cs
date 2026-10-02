namespace AniMeido.Plugin.Player.Sources.Packages;

internal sealed class SourcePackageManifest
{
    public int FormatVersion { get; set; }

    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string EntryFile { get; set; } = string.Empty;

    public string? SourceKind { get; set; }

    public string? SubscriptionId { get; set; }

    public string? UpstreamPath { get; set; }

    public string? UpstreamRevision { get; set; }
}
