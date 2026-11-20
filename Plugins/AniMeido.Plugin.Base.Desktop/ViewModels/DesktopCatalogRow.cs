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
        for (var rowIndex = 0; rowIndex < count; ++rowIndex)
        {
            var index = rowIndex * stride;
            var first = entries[index];
            var second = twoColumns && index + 1 < entries.Count ? entries[index + 1] : null;
            if (rowIndex < rows.Count)
            {
                var current = rows[rowIndex];
                if (current.FirstColumnSpan == (twoColumns ? 1 : 2)) current.Sync(first, second);
                else rows[rowIndex] = new(first, second, twoColumns);
            }
            else rows.Add(new(first, second, twoColumns));
        }
        while (rows.Count > count) rows.RemoveAt(rows.Count - 1);
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
        for (var rowIndex = 0; rowIndex < count; ++rowIndex)
        {
            var index = rowIndex * columns;
            var length = Math.Min(columns, entries.Count - index);
            if (rowIndex < rows.Count)
            {
                rows[rowIndex].MinimumHeight = minimumHeight;
                rows[rowIndex].Sync(entries, index, length);
            }
            else
            {
                var row = new DesktopCatalogPosterRow { MinimumHeight = minimumHeight };
                row.Sync(entries, index, length);
                rows.Add(row);
            }
        }
        while (rows.Count > count) rows.RemoveAt(rows.Count - 1);
    }

    private void Sync(IReadOnlyList<object> target, int start, int count)
    {
        if (Entries.Count == count)
        {
            var same = true;
            for (var i = 0; i < count && same; i++) same = Equals(Entries[i], target[start + i]);
            if (same) return;
        }

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
