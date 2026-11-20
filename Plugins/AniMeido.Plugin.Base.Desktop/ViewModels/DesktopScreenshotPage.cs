namespace AniMeido.Plugin.Base.ViewModels;

internal sealed record DesktopScreenshotPage<T>(
    IReadOnlyList<T> Items, int Index, int PageCount, int TotalCount)
{
    public bool HasPrevious => Index > 0;
    public bool HasNext => Index + 1 < PageCount;
}

internal static class DesktopScreenshotPaging
{
    private const int PageSize = 48;

    public static DesktopScreenshotPage<T> SelectContaining<T>(
        IReadOnlyList<T> items, Predicate<T> matches, int fallbackIndex = 0)
    {
        for (var index = 0; index < items.Count; ++index)
        {
            if (matches(items[index]))
                return Select(items, index / PageSize);
        }
        return Select(items, fallbackIndex);
    }

    public static DesktopScreenshotPage<T> Select<T>(
        IReadOnlyList<T> items, int index)
    {
        var pageCount = items.Count == 0 ? 1 : ((items.Count - 1) / PageSize) + 1;
        var current = Math.Clamp(index, 0, pageCount - 1);
        return new(
            items.Skip(current * PageSize).Take(PageSize).ToArray(),
            current,
            pageCount,
            items.Count);
    }
}
