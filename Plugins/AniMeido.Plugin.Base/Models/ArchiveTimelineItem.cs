namespace AniMeido.Plugin.Base.Models;

public sealed record ArchiveTimelineItem(
    DateTimeOffset OccurredAt,
    string Kind,
    string Heading,
    string Body,
    ArchiveEntry? Entry = null)
{
    public string DisplayTime => OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
