using System.Globalization;
using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>“我的番剧”的排序方式。</summary>
public enum MineSortKey
{
    RecentlyMarked,
    Title,
    Score,
}

/// <summary>“我的番剧”列表里的一行：作品、状态、标记时间与自己的评分。</summary>
public sealed record MineEntry(
    Anime Anime,
    AnimeTrackingStatus Status,
    DateTimeOffset? MarkedAt,
    double? MyRating,
    string MarkedText)
{
    public string StatusLabel => MineBrowse.StatusLabel(Status);

    public string MetaText => MineBrowse.MetaText(Anime);

    public bool HasMyRating => MyRating is not null;

    public string MyRatingText => MyRating is { } rating
        ? $"我的评分 {rating.ToString("0.#", CultureInfo.InvariantCulture)}"
        : "";
}

/// <summary>“我的番剧”的状态分组、行内文字与排序，不依赖 WinUI，便于测试。</summary>
public static class MineBrowse
{
    /// <summary>左侧平铺的状态，按使用频率排列。</summary>
    public static IReadOnlyList<AnimeTrackingStatus> MainStatuses { get; } =
    [
        AnimeTrackingStatus.Watching,
        AnimeTrackingStatus.PlanToWatch,
        AnimeTrackingStatus.Following,
        AnimeTrackingStatus.Completed,
        AnimeTrackingStatus.Dropped,
    ];

    /// <summary>“不想看到”分组，默认折叠。</summary>
    public static IReadOnlyList<AnimeTrackingStatus> HiddenStatuses { get; } =
    [
        AnimeTrackingStatus.NotInterested,
        AnimeTrackingStatus.Blocked,
    ];

    private static readonly IReadOnlyDictionary<AnimeTrackingStatus, string> Labels =
        TrackingStatusSection.CreateDefaults().ToDictionary(section => section.Status, section => section.Label);

    private static readonly CompareInfo TitleComparer = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;

    public static string StatusLabel(AnimeTrackingStatus status)
        => Labels.GetValueOrDefault(status, "");

    public static bool IsHidden(AnimeTrackingStatus status) => HiddenStatuses.Contains(status);

    /// <summary>tracking 表的时间来自写入或导入，格式不一；解析不了时返回 null。</summary>
    public static DateTimeOffset? ParseMarkedAt(string? raw)
        => DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
            : null;

    /// <summary>“标记于今天／昨天／3 天前／2 周前／5 个月前／1 年前”。</summary>
    public static string MarkedText(DateOnly? markedDay, DateOnly today)
    {
        if (markedDay is not { } day)
            return "";

        var days = today.DayNumber - day.DayNumber;
        return days switch
        {
            <= 0 => "标记于今天",
            1 => "标记于昨天",
            < 7 => $"标记于 {days} 天前",
            < 30 => $"标记于 {days / 7} 周前",
            < 365 => $"标记于 {days / 30} 个月前",
            _ => $"标记于 {days / 365} 年前",
        };
    }

    /// <summary>开播日期、形态与 Bangumi 评分，缺失的部分省略或写明。</summary>
    public static string MetaText(Anime anime)
    {
        var parts = new List<string>
        {
            anime.AirDate is { } date
                ? date.ToString("yyyy/M/d", CultureInfo.InvariantCulture)
                : "开播日期未定",
        };
        if (anime.MediaFormat != AnimeMediaFormat.Unknown)
            parts.Add(AnimeReleaseClassifier.GetMediaFormatFilterText(anime.MediaFormat));
        parts.Add(anime.Score is double score && score > 0
            ? $"Bangumi {score.ToString("0.0", CultureInfo.InvariantCulture)}"
            : "暂无评分");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 组装列表行：按 <paramref name="rows"/> 的顺序，跳过取不到详情的作品。
    /// </summary>
    public static IReadOnlyList<MineEntry> BuildEntries(
        IEnumerable<(int AnimeId, AnimeTrackingStatus Status, DateTimeOffset? MarkedAt)> rows,
        IReadOnlyDictionary<int, Anime> animeById,
        IReadOnlyDictionary<int, double> myRatings,
        DateOnly today)
        => rows
            .Where(row => animeById.ContainsKey(row.AnimeId))
            .Select(row => new MineEntry(
                animeById[row.AnimeId],
                row.Status,
                row.MarkedAt,
                myRatings.TryGetValue(row.AnimeId, out var rating) ? rating : null,
                MarkedText(
                    row.MarkedAt is { } at ? DateOnly.FromDateTime(at.LocalDateTime) : null,
                    today)))
            .ToList();

    /// <summary>最近标记在前；标题按中文拼音；评分从高到低。缺时间或评分的排在最后，同值保持原顺序。</summary>
    public static IReadOnlyList<MineEntry> Sort(IEnumerable<MineEntry> entries, MineSortKey key)
    {
        var list = entries.ToList();
        return key switch
        {
            MineSortKey.Title => list
                .OrderBy(entry => entry.Anime.Title, TitleComparer.GetStringComparer(CompareOptions.None))
                .ToList(),
            MineSortKey.Score => list
                .Where(entry => entry.Anime.Score is > 0)
                .OrderByDescending(entry => entry.Anime.Score)
                .Concat(list.Where(entry => entry.Anime.Score is not > 0))
                .ToList(),
            _ => list
                .Where(entry => entry.MarkedAt is not null)
                .OrderByDescending(entry => entry.MarkedAt)
                .Concat(list.Where(entry => entry.MarkedAt is null))
                .ToList(),
        };
    }

    /// <summary>各状态的数量；没有记录的状态为 0。</summary>
    public static IReadOnlyDictionary<AnimeTrackingStatus, int> CountByStatus(
        IEnumerable<AnimeTrackingStatus> statuses)
    {
        var counts = MainStatuses.Concat(HiddenStatuses).ToDictionary(status => status, _ => 0);
        foreach (var status in statuses)
        {
            if (counts.ContainsKey(status))
                counts[status]++;
        }

        return counts;
    }
}
