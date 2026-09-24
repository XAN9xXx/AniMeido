using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>搜索页结果的排序方式。</summary>
public enum SearchSortKey
{
    Match,
    Score,
    AirDate,
}

/// <summary>搜索页的最近搜索、分页合并与排序，不依赖 WinUI，便于测试。</summary>
public static class SearchBrowse
{
    /// <summary>最近搜索最多保留的条数。</summary>
    public const int RecentLimit = 8;

    /// <summary>把这次搜索放到最前；同一个词（不分大小写）只保留一次，超出上限的旧词丢弃。</summary>
    public static IReadOnlyList<string> PushRecent(IReadOnlyList<string> recent, string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return recent;

        return new[] { trimmed }
            .Concat(recent.Where(item => !string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase)))
            .Take(RecentLimit)
            .ToList();
    }

    /// <summary>接上下一页；分页边界可能重复返回同一部作品，按 ID 去重并保持先后顺序。</summary>
    public static IReadOnlyList<Anime> AppendPage(IReadOnlyList<Anime> loaded, IEnumerable<Anime> page)
    {
        var seen = loaded.Select(anime => anime.ID).ToHashSet();
        return loaded.Concat(page.Where(anime => seen.Add(anime.ID))).ToList();
    }

    /// <summary>匹配度保持 Bangumi 返回的顺序；评分从高到低；开播日期从新到旧。缺失的排在最后。</summary>
    public static IReadOnlyList<PastSeasonEntry> Sort(IEnumerable<PastSeasonEntry> entries, SearchSortKey key)
        => key switch
        {
            SearchSortKey.Score => PastSeasonBrowse.Sort(entries, PastSeasonSortKey.Score, ascending: false),
            SearchSortKey.AirDate => PastSeasonBrowse.Sort(entries, PastSeasonSortKey.AirDate, ascending: false),
            _ => entries.ToList(),
        };

    /// <summary>“共 128 部 · 已加载 40 部”；全部加载后只写总数。</summary>
    public static string Summary(int total, int loaded)
        => loaded >= total
            ? $"共 {total} 部"
            : $"共 {total} 部 · 已加载 {loaded} 部";
}
