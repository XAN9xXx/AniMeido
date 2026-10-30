using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.Services;

/// <summary>标签结果页的一次完整查询；只排序一次，使用资料源既有的批量补全能力。</summary>
public sealed class TagSearchResults(MetadataDataSource source) : IAsyncDisposable
{
    private readonly ServiceOperationLifetime _lifetime = new();

    public async Task<List<Anime>> LoadAsync(string tag,
        int fromYear, int toYear, CancellationToken ct)
    {
        using var operation = _lifetime.Begin(ct);
        return await Task.Run(() => source.SearchAllByTagAsync(tag, "rank",
            $"{fromYear:D4}-01-01", $"{toYear + 1:D4}-01-01", operation.Token), operation.Token)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _lifetime.StopAsync();

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
