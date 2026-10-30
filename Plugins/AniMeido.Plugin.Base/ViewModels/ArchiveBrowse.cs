using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;

namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>档案页的筛选与时间线投影；不持有控件、选中项或页面生命周期。</summary>
internal static class ArchiveBrowse
{
    internal static IReadOnlyList<ArchiveListItem> FilterArchives(
        IEnumerable<ArchiveListItem> archives,
        string filter,
        AnimeTrackingStatus? status,
        double? minimumRating,
        int? year)
    {
        return archives.Where(item =>
            item.TrackingStatus
                != AnimeTrackingStatus.Blocked
            && (string.IsNullOrWhiteSpace(filter)
                || item.Archive.TitleSnapshot.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)
                || item.Tags.Any(tag => tag.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)))
            && (status is null || item.TrackingStatus == status)
            && (minimumRating is null
                || item.Archive.PersonalRating >= minimumRating)
            && (year is null
                || item.Archive.CreatedAt.ToLocalTime().Year == year))
            .ToList();
    }

    internal static IReadOnlyList<AnimeScreenshot> FilterScreenshots(
        IEnumerable<AnimeScreenshot> screenshots,
        IReadOnlyDictionary<string, IReadOnlyList<string>> tagsByScreenshot,
        string filter,
        int? year)
    {
        return screenshots.Where(item =>
            (year is null
                || item.CapturedAt.ToLocalTime().Year == year)
            && (filter.Length == 0
                || (item.AnimeTitle?.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase) ?? false)
                || item.ContextNote.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)
                || item.ProcessName.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)
                || (tagsByScreenshot.TryGetValue(
                        item.ScreenshotId,
                        out var tags)
                    && tags.Any(tag => tag.Contains(
                        filter,
                        StringComparison.CurrentCultureIgnoreCase)))))
            .ToArray();
    }

    internal static IReadOnlyList<ArchiveTimelineItem> BuildTimeline(
        IReadOnlyList<ArchiveEntry> entries,
        IReadOnlyList<WatchHistoryItem> history,
        IReadOnlyList<ArchiveTrackingChange> changes,
        bool includePlayback)
    {
        return entries.Select(entry => new ArchiveTimelineItem(
                entry.OccurredAt,
                "感想",
                string.IsNullOrWhiteSpace(entry.EpisodeText)
                    ? "观看感想"
                    : entry.EpisodeText,
                entry.Body,
                entry))
            .Concat((includePlayback ? history : [])
                .Select(item => new ArchiveTimelineItem(
                item.OccurredAt,
                "观看记录",
                $"{item.EpisodeText} · {item.SourceText}",
                item.Note)))
            .Concat(changes.Select(change => new ArchiveTimelineItem(
                change.ChangedAt,
                "状态变更",
                $"标记为{FormatTrackingStatus(change.NewStatus)}",
                string.Empty)))
            .OrderByDescending(item => item.OccurredAt)
            .ToArray();
    }

    private static string FormatTrackingStatus(
        AnimeTrackingStatus status)
        => status switch
        {
            AnimeTrackingStatus.Watching => "追番中",
            AnimeTrackingStatus.PlanToWatch => "补番中",
            AnimeTrackingStatus.Following => "关注",
            AnimeTrackingStatus.Completed => "已看完",
            AnimeTrackingStatus.Dropped => "弃坑",
            AnimeTrackingStatus.NotInterested => "不感兴趣",
            AnimeTrackingStatus.Blocked => "屏蔽",
            _ => "无标记",
        };

}
