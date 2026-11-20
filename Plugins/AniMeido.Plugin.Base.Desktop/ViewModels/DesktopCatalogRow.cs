namespace AniMeido.Plugin.Base.ViewModels;

public sealed record DesktopCatalogRow(object First, object? Second, bool TwoColumns)
{
    public bool HasSecond => Second is not null;
    public int FirstColumnSpan => TwoColumns ? 1 : 2;

    public static void SyncRows(System.Collections.ObjectModel.ObservableCollection<DesktopCatalogRow> rows,
        IReadOnlyList<object> entries, bool twoColumns)
    {
        var stride = twoColumns ? 2 : 1;
        var count = (entries.Count + stride - 1) / stride;
        for (var rowIndex = 0; rowIndex < count; ++rowIndex)
        {
            var index = rowIndex * stride;
            var first = entries[index];
            var second = twoColumns && index + 1 < entries.Count ? entries[index + 1] : null;
            if (rowIndex < rows.Count)
            {
                var current = rows[rowIndex];
                if (ReferenceEquals(current.First, first) && ReferenceEquals(current.Second, second)
                    && current.TwoColumns == twoColumns)
                    continue;
                rows[rowIndex] = new(first, second, twoColumns);
            }
            else
                rows.Add(new(first, second, twoColumns));
        }
        while (rows.Count > count)
            rows.RemoveAt(rows.Count - 1);
    }
}

public sealed record DesktopCatalogPosterRow(IReadOnlyList<object> Entries)
{
    public static void SyncRows(System.Collections.ObjectModel.ObservableCollection<DesktopCatalogPosterRow> rows,
        IReadOnlyList<object> entries, int columns)
    {
        columns = Math.Max(1, columns);
        var count = (entries.Count + columns - 1) / columns;
        for (var rowIndex = 0; rowIndex < count; ++rowIndex)
        {
            var index = rowIndex * columns;
            var rowEntries = entries.Skip(index).Take(columns).ToArray();
            if (rowIndex < rows.Count)
            {
                if (rows[rowIndex].Entries.Count == rowEntries.Length
                    && rows[rowIndex].Entries.Zip(rowEntries).All(pair => ReferenceEquals(pair.First, pair.Second)))
                    continue;
                rows[rowIndex] = new(rowEntries);
            }
            else
                rows.Add(new(rowEntries));
        }
        while (rows.Count > count)
            rows.RemoveAt(rows.Count - 1);
    }
}
