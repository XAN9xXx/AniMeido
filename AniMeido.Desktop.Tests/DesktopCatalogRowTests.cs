using AniMeido.Plugin.Base.ViewModels;
using Xunit;
using System.Collections.ObjectModel;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopCatalogRowTests
{
    [Fact]
    public void PosterRowsKeepTheCatalogOrderAndOddTail()
    {
        object[] entries = [new(), new(), new(), new(), new()];
        var rows = new ObservableCollection<DesktopCatalogPosterRow>();

        DesktopCatalogPosterRow.SyncRows(rows, entries, columns: 3, minimumHeight: 278);

        Assert.Equal(2, rows.Count);
        Assert.Equal(entries.Take(3), rows[0].Entries);
        Assert.Equal(entries.Skip(3), rows[1].Entries);
    }

    [Fact]
    public void PosterRowsRetainTheirIdentityAndUpdateTheMinimumHeightAtNewWidths()
    {
        object[] entries = [new(), new(), new(), new()];
        var rows = new ObservableCollection<DesktopCatalogPosterRow>();
        DesktopCatalogPosterRow.SyncRows(rows, entries, columns: 3, minimumHeight: 278);
        var first = rows[0];
        var last = rows[1];
        DesktopCatalogPosterRow.SyncRows(rows, entries.ToArray(), columns: 3, minimumHeight: 278);
        Assert.Same(first, rows[0]);
        Assert.Same(last, rows[1]);

        DesktopCatalogPosterRow.SyncRows(rows, entries, columns: 1, minimumHeight: 218);
        Assert.Equal(4, rows.Count);
        Assert.Same(first, rows[0]);
        Assert.All(rows, row => Assert.Equal(218, row.MinimumHeight));
        Assert.Equal(entries, rows.SelectMany(row => row.Entries));
    }

    [Fact]
    public void TwoColumnRowsKeepTheOriginalPairingAndOrder()
    {
        object[] entries = [new(), new(), new(), new()];

        var rows = CreateRows(entries, twoColumns: true);

        Assert.Equal(2, rows.Count);
        Assert.Same(entries[0], rows[0].First);
        Assert.Same(entries[1], rows[0].Second);
        Assert.Same(entries[2], rows[1].First);
        Assert.Same(entries[3], rows[1].Second);
        Assert.All(rows, row => Assert.True(row.HasSecond));
    }

    [Fact]
    public void OddTailKeepsItsHalfWidthAndHasNoEmptySecondCard()
    {
        object[] entries = [new(), new(), new()];

        var rows = CreateRows(entries, twoColumns: true);

        Assert.Equal(2, rows.Count);
        Assert.Same(entries[2], rows[1].First);
        Assert.Null(rows[1].Second);
        Assert.False(rows[1].HasSecond);
        Assert.Equal(1, rows[1].FirstColumnSpan);
    }

    [Fact]
    public void SingleColumnRowsKeepEveryEntryAtFullWidth()
    {
        object[] entries = [new(), new(), new()];

        var rows = CreateRows(entries, twoColumns: false);

        Assert.Equal(entries.Length, rows.Count);
        for (var index = 0; index < entries.Length; ++index)
        {
            Assert.Same(entries[index], rows[index].First);
            Assert.Null(rows[index].Second);
            Assert.False(rows[index].HasSecond);
            Assert.Equal(2, rows[index].FirstColumnSpan);
        }
    }

    [Fact]
    public void EmptyCatalogProducesNoRows()
        => Assert.Empty(CreateRows([], twoColumns: true));

    [Fact]
    public void ReturningWithTheSameEntriesKeepsExistingRowsAndSendsNoCollectionChanges()
    {
        object[] entries = [new(), new(), new(), new()];
        var rows = CreateRows(entries, twoColumns: true);
        var first = rows[0];
        var second = rows[1];
        var changes = 0;
        rows.CollectionChanged += (_, _) => ++changes;

        DesktopCatalogRow.SyncRows(rows, entries.ToArray(), twoColumns: true);

        Assert.Same(first, rows[0]);
        Assert.Same(second, rows[1]);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void AStatusUpdateChangesOnlyTheMatchingSlot()
    {
        object[] entries = [new(), new(), new(), new()];
        var rows = CreateRows(entries, twoColumns: true);
        var matching = rows[0];
        var unchanged = rows[1];
        var updatedEntry = new object();
        entries[1] = updatedEntry;
        var changes = 0;
        rows.CollectionChanged += (_, _) => ++changes;
        var changedSlots = new List<string?>();
        matching.PropertyChanged += (_, args) => changedSlots.Add(args.PropertyName);

        DesktopCatalogRow.SyncRows(rows, entries, twoColumns: true);

        Assert.Same(entries[0], rows[0].First);
        Assert.Same(updatedEntry, rows[0].Second);
        Assert.Same(matching, rows[0]);
        Assert.Same(unchanged, rows[1]);
        Assert.Equal(0, changes);
        Assert.Contains(nameof(DesktopCatalogRow.Second), changedSlots);
        Assert.DoesNotContain(nameof(DesktopCatalogRow.First), changedSlots);
    }

    [Fact]
    public void RemovingAndAddingTheTailKeepsTheLeadingRow()
    {
        object[] entries = [new(), new(), new(), new()];
        var rows = CreateRows(entries, twoColumns: true);
        var leading = rows[0];

        DesktopCatalogRow.SyncRows(rows, entries.Take(2).ToArray(), twoColumns: true);
        Assert.Single(rows);
        Assert.Same(leading, rows[0]);

        DesktopCatalogRow.SyncRows(rows, entries, twoColumns: true);
        Assert.Equal(2, rows.Count);
        Assert.Same(leading, rows[0]);
        Assert.Same(entries[2], rows[1].First);
        Assert.Same(entries[3], rows[1].Second);
    }

    [Fact]
    public void ReturningAfterOffPageChangesSynchronizesTheKeptCollection()
    {
        object[] original = [new(), new(), new(), new()];
        var rows = CreateRows(original, twoColumns: true);
        var leading = rows[0];
        object[] latest = [original[0], original[1], new object()];

        DesktopCatalogRow.SyncRows(rows, latest, twoColumns: true);

        Assert.Equal(2, rows.Count);
        Assert.Same(leading, rows[0]);
        Assert.Same(latest[2], rows[1].First);
        Assert.Null(rows[1].Second);
        Assert.Equal(1, rows[1].FirstColumnSpan);
    }

    private static ObservableCollection<DesktopCatalogRow> CreateRows(IReadOnlyList<object> entries, bool twoColumns)
    {
        var rows = new ObservableCollection<DesktopCatalogRow>();
        DesktopCatalogRow.SyncRows(rows, entries, twoColumns);
        return rows;
    }
}
