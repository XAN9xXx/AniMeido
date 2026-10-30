using System.Text.Json.Serialization;

namespace AniMeido.Plugin.Player.Sources.Subscriptions;

internal enum SourceSubscriptionKind
{
    EasyBangumi,
    AnimekoWeb,
}

internal enum SubscriptionChangeKind
{
    Added,
    Updated,
    Unchanged,
    Skipped,
    Invalid,
    Orphaned,
}

internal sealed class SourceSubscriptionState
{
    public string Id { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public SourceSubscriptionKind Kind { get; set; }

    public DateTimeOffset? LastRefreshUtc { get; set; }

    public List<SubscriptionSourceState> Sources { get; set; } = [];

    public override string ToString()
        => $"{Kind} · {Url}"
            + (LastRefreshUtc is null
                ? " · 尚未刷新"
                : $" · {LastRefreshUtc.Value.LocalDateTime:g}");
}

internal sealed class SubscriptionSourceState
{
    public string UpstreamPath { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Revision { get; set; } = string.Empty;

    public string? UpstreamBlobId { get; set; }

    public int RevisionNumber { get; set; }

    public bool IsOrphaned { get; set; }
}

internal sealed class SourceSubscriptionDocument
{
    public int FormatVersion { get; set; } = 1;

    public List<SourceSubscriptionState> Subscriptions { get; set; } = [];
}

internal sealed record SubscriptionPreviewItem(
    string UpstreamPath,
    string SourceId,
    string DisplayName,
    string Revision,
    int RevisionNumber,
    SubscriptionChangeKind Change,
    string? Message = null)
{
    [JsonIgnore]
    internal string? Content { get; init; }

    [JsonIgnore]
    internal string? UpstreamBlobId { get; init; }

    public string DisplayText
        => $"{Change}: {DisplayName}"
            + (string.IsNullOrWhiteSpace(Message)
                ? string.Empty
                : $" · {Message}");

    public override string ToString() => DisplayText;
}

internal sealed record SourceSubscriptionPreview(
    string SubscriptionId,
    string Url,
    SourceSubscriptionKind Kind,
    IReadOnlyList<SubscriptionPreviewItem> Items)
{
    public int ApplicableCount => Items.Count(item =>
        item.Change is SubscriptionChangeKind.Added
            or SubscriptionChangeKind.Updated
            or SubscriptionChangeKind.Orphaned);
}

internal sealed record SubscriptionItemResult(string Path, string DisplayName, bool Applied, string? Error = null);

internal sealed record SubscriptionApplyResult(
    IReadOnlyList<SubscriptionItemResult> Items, bool Cancelled = false, string? Warning = null)
{
    public int AppliedCount => Items.Count(item => item.Applied);
    public int FailedCount => Items.Count(item => !item.Applied);
}
