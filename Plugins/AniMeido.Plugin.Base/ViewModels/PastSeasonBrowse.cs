using System.Collections.ObjectModel;
using System.Globalization;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;

namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>番剧库的排序方式。</summary>
public enum PastSeasonSortKey
{
    Score,
    AirDate,
    Title,
}

/// <summary>番剧库网格里的一项：作品与它的追番状态。</summary>
public sealed record PastSeasonEntry(Anime Anime, AnimeTrackingStatus Status);

/// <summary>形态筛选标签；<see cref="Format"/> 为空表示“全部”。</summary>
public sealed record PastSeasonFormatChip(
    AnimeMediaFormat? Format,
    string Label,
    int Count,
    bool IsSelected);

/// <summary>
/// 番剧库的筛选、计数、排序与季度前后切换，不依赖 WinUI，便于测试。
/// 筛选顺序：去掉屏蔽 → 标题搜索 → 隐藏看过 → 形态。形态计数取形态筛选之前的结果，
/// 这样标签上的数字就是点下去后看到的部数。
/// </summary>
public static class PastSeasonBrowse
{
    /// <summary>形态标签的固定顺序；未知形态归入“其他”。</summary>
    public static IReadOnlyList<AnimeMediaFormat> FormatOrder { get; } =
    [
        AnimeMediaFormat.Television,
        AnimeMediaFormat.Ona,
        AnimeMediaFormat.Movie,
        AnimeMediaFormat.Ova,
        AnimeMediaFormat.Unknown,
    ];

    private static readonly CompareInfo TitleComparer = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;

    /// <summary>形态筛选之前的可见作品：去掉屏蔽、按标题搜索、按需隐藏看过。</summary>
    public static IReadOnlyList<PastSeasonEntry> Visible(
        IEnumerable<Anime> source,
        IReadOnlyDictionary<int, AnimeTrackingStatus> statuses,
        string? query,
        bool hideCompleted)
    {
        var trimmed = query?.Trim();
        return source
            .Select(anime => new PastSeasonEntry(
                anime,
                statuses.TryGetValue(anime.ID, out var status) ? status : AnimeTrackingStatus.None))
            .Where(entry => entry.Status != AnimeTrackingStatus.Blocked)
            .Where(entry => string.IsNullOrEmpty(trimmed)
                || entry.Anime.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .Where(entry => !hideCompleted || entry.Status != AnimeTrackingStatus.Completed)
            .ToList();
    }

    /// <summary>未列出的形态一律按“其他”计。</summary>
    public static AnimeMediaFormat NormalizeFormat(AnimeMediaFormat format)
        => FormatOrder.Contains(format) ? format : AnimeMediaFormat.Unknown;

    /// <summary>“全部”加上各形态；数量为 0 的形态不显示，除非它正被选中。</summary>
    public static IReadOnlyList<PastSeasonFormatChip> FormatChips(
        IReadOnlyList<PastSeasonEntry> visible,
        AnimeMediaFormat? selected)
    {
        var counts = visible
            .GroupBy(entry => NormalizeFormat(entry.Anime.MediaFormat))
            .ToDictionary(group => group.Key, group => group.Count());
        var chips = new List<PastSeasonFormatChip>
        {
            new(null, "全部", visible.Count, selected is null),
        };
        foreach (var format in FormatOrder)
        {
            var count = counts.GetValueOrDefault(format);
            if (count > 0 || selected == format)
            {
                chips.Add(new(
                    format,
                    AnimeReleaseClassifier.GetMediaFormatFilterText(format),
                    count,
                    selected == format));
            }
        }

        return chips;
    }

    public static IEnumerable<PastSeasonEntry> ByFormat(
        IEnumerable<PastSeasonEntry> visible,
        AnimeMediaFormat? format)
        => format is not { } selected
            ? visible
            : visible.Where(entry => NormalizeFormat(entry.Anime.MediaFormat) == selected);

    /// <summary>评分从高到低、日期从早到晚、标题从 A 到 Z。</summary>
    public static bool DefaultAscending(PastSeasonSortKey key) => key != PastSeasonSortKey.Score;

    /// <summary>排序方向按钮的说明文字。</summary>
    public static string SortDirectionText(PastSeasonSortKey key, bool ascending) => key switch
    {
        PastSeasonSortKey.Score => ascending ? "评分从低到高" : "评分从高到低",
        PastSeasonSortKey.AirDate => ascending ? "开播从早到晚" : "开播从晚到早",
        _ => ascending ? "标题 A → Z" : "标题 Z → A",
    };

    /// <summary>缺评分或缺日期的作品无论升降序都排在最后；同值保持原顺序。</summary>
    public static IReadOnlyList<PastSeasonEntry> Sort(
        IEnumerable<PastSeasonEntry> entries,
        PastSeasonSortKey key,
        bool ascending)
    {
        var list = entries.ToList();
        return key switch
        {
            PastSeasonSortKey.Score => SortWithMissingLast(
                list,
                entry => entry.Anime.Score is > 0 ? entry.Anime.Score : null,
                ascending),
            PastSeasonSortKey.AirDate => SortWithMissingLast(
                list,
                entry => entry.Anime.AirDate,
                ascending),
            _ => ascending
                ? list.OrderBy(entry => entry.Anime.Title, TitleComparer.GetStringComparer(CompareOptions.None)).ToList()
                : list.OrderByDescending(entry => entry.Anime.Title, TitleComparer.GetStringComparer(CompareOptions.None)).ToList(),
        };
    }

    private static IReadOnlyList<PastSeasonEntry> SortWithMissingLast<T>(
        IReadOnlyList<PastSeasonEntry> entries,
        Func<PastSeasonEntry, T?> selector,
        bool ascending)
        where T : struct, IComparable<T>
    {
        var present = entries.Where(entry => selector(entry).HasValue);
        var ordered = ascending
            ? present.OrderBy(entry => selector(entry)!.Value)
            : present.OrderByDescending(entry => selector(entry)!.Value);
        return ordered
            .Concat(entries.Where(entry => !selector(entry).HasValue))
            .ToList();
    }

    /// <summary>
    /// 把 <paramref name="current"/> 原地改成 <paramref name="target"/>：移除不再显示的、插入新出现的、
    /// 替换状态变了的，其余保持不动，网格因此不会回到顶部。
    /// 任一方有重复作品时无法按作品对应，返回 false，由调用方整体替换。
    /// </summary>
    public static bool SyncInPlace(
        ObservableCollection<PastSeasonEntry> current,
        IReadOnlyList<PastSeasonEntry> target)
        => CollectionSync.SyncInPlace(current, target, entry => entry.Anime.ID);

    /// <summary>前后移动若干季；超出 [<paramref name="earliest"/>, <paramref name="latest"/>] 时返回 null。</summary>
    public static PastSeasonTarget? Step(
        PastSeasonTarget current,
        int delta,
        PastSeasonTarget earliest,
        PastSeasonTarget latest)
    {
        var index = Index(current) + delta;
        if (index < Index(earliest) || index > Index(latest))
            return null;

        return new PastSeasonTarget(
            Math.DivRem(index, 4, out var remainder),
            (Season)(remainder + 1));
    }

    /// <summary>把季度限制在可浏览范围内。</summary>
    public static PastSeasonTarget Clamp(
        PastSeasonTarget target,
        PastSeasonTarget earliest,
        PastSeasonTarget latest)
        => Index(target) < Index(earliest)
            ? earliest
            : Index(target) > Index(latest) ? latest : target;

    public static bool IsWithin(
        PastSeasonTarget target,
        PastSeasonTarget earliest,
        PastSeasonTarget latest)
        => Index(target) >= Index(earliest) && Index(target) <= Index(latest);

    private static int Index(PastSeasonTarget target) => target.Year * 4 + (int)target.Season - 1;

    public static string SeasonName(Season season) => season switch
    {
        Season.Winter => "冬",
        Season.Spring => "春",
        Season.Summer => "夏",
        Season.Fall => "秋",
        _ => "",
    };

    public static string SeasonMonths(Season season) => season switch
    {
        Season.Winter => "1–3月",
        Season.Spring => "4–6月",
        Season.Summer => "7–9月",
        Season.Fall => "10–12月",
        _ => "",
    };

    /// <summary>“2026 年春季 · 247 部”；筛选后为“2026 年春季 · 显示 12 / 247 部”。</summary>
    public static string Summary(PastSeasonTarget season, int shown, int total)
    {
        var name = $"{season.Year} 年{SeasonName(season.Season)}季";
        return shown == total
            ? $"{name} · {total} 部"
            : $"{name} · 显示 {shown} / {total} 部";
    }
}
