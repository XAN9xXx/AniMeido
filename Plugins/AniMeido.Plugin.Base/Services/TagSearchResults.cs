using AniMeido.Contracts;
using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.Services;

internal static class TagSearchResults
{
    internal static async Task<List<Anime>> LoadAsync(IAnimeDataSource source, string tag,
        int fromYear, int toYear, CancellationToken ct)
    {
        var results = new List<Anime>();
        var seen = new HashSet<int>();
        for (var offset = 0; ; offset += 20)
        {
            ct.ThrowIfCancellationRequested();
            var (page, total) = await source.SearchByTagAsync(tag, offset, "rank", ct,
                $"{fromYear:D4}-01-01", $"{toYear + 1:D4}-01-01").ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            results.AddRange(page.Where(anime => seen.Add(anime.ID)));
            // 批量补全可能漏掉刚停用的作品，不能用 page.Count < 20 提前截断。
            if (offset + 20 >= total) return results;
        }
    }

    internal static List<Anime> Sort(List<Anime> items, string sort, bool descending)
    {
        if (sort == "rank")
        {
            var scoredFirst = items.OrderBy(a => a.Score.HasValue ? 0 : 1);
            // 数据源已按分数、人数给出整段日期的顺序，稳定排序保留同分人数次序。
            return descending ? scoredFirst.ThenByDescending(a => a.Score).ToList()
                : scoredFirst.ThenBy(a => a.Score).ToList();
        }
        var sorted = sort == "date"
            ? items.OrderBy(a => a.AirDate.HasValue ? 0 : 1).ThenBy(a => a.AirDate ?? DateOnly.MinValue)
            : items.OrderBy(a => 0);
        return descending ? sorted.Reverse().ToList() : sorted.ToList();
    }
}
