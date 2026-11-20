using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AniMeido.Plugin.Base.ViewModels;

public sealed class DesktopCatalogRow(object first, object? second, bool twoColumns) : ObservableObject
{
    private object _first = first;
    private object? _second = second;
    public object First => _first;
    public object? Second => _second;
    public bool HasSecond => _second is not null;
    public int FirstColumnSpan => twoColumns ? 1 : 2;

    private void Sync(object a, object? b)
    {
        SetProperty(ref _first, a, nameof(First));
        if (SetProperty(ref _second, b, nameof(Second))) OnPropertyChanged(nameof(HasSecond));
    }

    public static void SyncRows(ObservableCollection<DesktopCatalogRow> rows, IReadOnlyList<object> entries, bool twoColumns)
    {
        var stride = twoColumns ? 2 : 1;
        var count = (entries.Count + stride - 1) / stride;
        var shared = Math.Min(rows.Count, count);
        var suffix = 0;
        while (rows.Count != count && suffix < shared)
        {
            var start = (count - suffix - 1) * stride;
            var row = rows[rows.Count - suffix - 1];
            if (row.FirstColumnSpan != (twoColumns ? 1 : 2) || !Equals(row.First, entries[start])
                || !Equals(row.Second, twoColumns && start + 1 < entries.Count ? entries[start + 1] : null)) break;
            ++suffix;
        }
        for (var rowIndex = 0; rowIndex < count - suffix; ++rowIndex)
        {
            var index = rowIndex * stride;
            var first = entries[index];
            var second = twoColumns && index + 1 < entries.Count ? entries[index + 1] : null;
            if (rowIndex < shared - suffix)
            {
                var current = rows[rowIndex];
                if (current.FirstColumnSpan == (twoColumns ? 1 : 2)) current.Sync(first, second);
                else rows[rowIndex] = new(first, second, twoColumns);
            }
            else rows.Insert(rowIndex, new(first, second, twoColumns));
        }
        while (rows.Count > count) rows.RemoveAt(rows.Count - suffix - 1);
    }
}

public sealed class DesktopCatalogPosterRow : ObservableObject
{
    private double _minimumHeight = 278;
    public ObservableCollection<object> Entries { get; } = [];
    public double MinimumHeight { get => _minimumHeight; private set => SetProperty(ref _minimumHeight, value); }

    public static void SyncRows(ObservableCollection<DesktopCatalogPosterRow> rows, IReadOnlyList<object> entries,
        int columns, double minimumHeight)
    {
        columns = Math.Max(1, columns);
        var count = (entries.Count + columns - 1) / columns;
        // 筛选可能只删掉中间一整排。保留后方内容未变的行，避免逐张替换这些封面。
        var shared = Math.Min(rows.Count, count);
        var suffix = 0;
        if (rows.Count != count)
        {
            while (suffix < shared)
            {
                var start = (count - suffix - 1) * columns;
                var row = rows[rows.Count - suffix - 1];
                if (!row.Matches(entries, start, Math.Min(columns, entries.Count - start))) break;
                row.MinimumHeight = minimumHeight;
                ++suffix;
            }
        }
        for (var rowIndex = 0; rowIndex < count - suffix; ++rowIndex)
        {
            var index = rowIndex * columns;
            var length = Math.Min(columns, entries.Count - index);
            if (rowIndex < shared - suffix)
            {
                rows[rowIndex].MinimumHeight = minimumHeight;
                rows[rowIndex].Sync(entries, index, length);
            }
            else
            {
                var row = new DesktopCatalogPosterRow { MinimumHeight = minimumHeight };
                row.Sync(entries, index, length);
                rows.Insert(rowIndex, row);
            }
        }
        while (rows.Count > count) rows.RemoveAt(rows.Count - suffix - 1);
    }

    private bool Matches(IReadOnlyList<object> target, int start, int count)
    {
        if (Entries.Count != count) return false;
        for (var i = 0; i < count; i++)
            if (!Equals(Entries[i], target[start + i])) return false;
        return true;
    }

    private void Sync(IReadOnlyList<object> target, int start, int count)
    {
        if (Matches(target, start, count)) return;

        // 先移除不再显示的作品；同排保留项由 ItemsControl 原地挪位，不重新建立封面源。
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var keep = false;
            for (var j = 0; j < count && !keep; j++) keep = Equals(Entries[i], target[start + j]);
            if (!keep) Entries.RemoveAt(i);
        }
        for (var i = 0; i < count; i++)
        {
            var item = target[start + i];
            if (i < Entries.Count && Equals(Entries[i], item)) continue;
            var previousIndex = Entries.IndexOf(item);
            if (previousIndex < 0) Entries.Insert(i, item);
            else Entries.Move(previousIndex, i);
        }
    }
}
