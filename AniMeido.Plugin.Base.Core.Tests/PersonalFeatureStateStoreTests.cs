using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class PersonalFeatureStateStoreTests : IAsyncLifetime
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task TypedStatesRoundTripIndependentlyThroughTheSameStore()
    {
        var store = new PersonalFeatureStateStore(_database.Factory);
        var date = new DateOnly(2026, 10, 28);
        var zone = new DragZoneConfig
        {
            Id = "custom", Label = "Custom zone", Action = DragAction.Blocked,
            XPercent = 0.1, YPercent = 0.2, WidthPercent = 0.3, HeightPercent = 0.4,
        };
        await store.SaveDragZoneConfigAsync([zone]).WaitAsync(TestBudget);
        await store.SaveCalendarDailyPickAsync(date, -7).WaitAsync(TestBudget);
        await store.SaveTimeMachineShownAsync(new Dictionary<(int Year, Season Season), IReadOnlyList<int>>
        {
            [(2024, Season.Winter)] = [9, 3],
        }).WaitAsync(TestBudget);
        await store.SaveTodayThemeAsync(new TodayThemeState(
            date, TodayThemeKind.OneYearAgoToday, true, [2, 2, 1])).WaitAsync(TestBudget);
        await store.SaveRecentSearchesAsync(["new", "old", "new"]).WaitAsync(TestBudget);

        var loadedZone = Assert.Single(await store.LoadDragZoneConfigAsync().WaitAsync(TestBudget));
        Assert.Equal(("custom", "Custom zone", DragAction.Blocked, 0.1, 0.2, 0.3, 0.4),
            (loadedZone.Id, loadedZone.Label, loadedZone.Action, loadedZone.XPercent,
                loadedZone.YPercent, loadedZone.WidthPercent, loadedZone.HeightPercent));
        (DateOnly Date, int AnimeId)? expectedPick = (date, -7);
        Assert.Equal(expectedPick, await store.LoadCalendarDailyPickAsync().WaitAsync(TestBudget));
        var shown = await store.LoadTimeMachineShownAsync().WaitAsync(TestBudget);
        Assert.Single(shown);
        Assert.Equal(new[] { 9, 3 }, shown[(2024, Season.Winter)]);
        var theme = await store.LoadTodayThemeAsync().WaitAsync(TestBudget);
        Assert.NotNull(theme);
        Assert.Equal(date, theme.Date);
        Assert.Equal(TodayThemeKind.OneYearAgoToday, theme.Theme);
        Assert.True(theme.IsFallback);
        Assert.Equal(new[] { 2, 2, 1 }, theme.AnimeIds);
        Assert.Equal(new[] { "new", "old", "new" }, await store.LoadRecentSearchesAsync().WaitAsync(TestBudget));
    }

    [Fact]
    public async Task SnapshotDragZoneReaderUsesTheCallersTransactionWithoutCommittingIt()
    {
        using var connection = await _database.Factory.OpenAsync().WaitAsync(TestBudget);
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO config(Key, Value) VALUES('drag_zones', @json)";
            command.Parameters.AddWithValue("@json", """
                [{"id":"uncommitted","label":"Snapshot zone","action":4,
                  "xPercent":0.1,"yPercent":0.2,"widthPercent":0.3,"heightPercent":0.4}]
                """);
            await command.ExecuteNonQueryAsync().WaitAsync(TestBudget);
        }

        var zones = await PersonalFeatureStateStore.LoadDragZoneConfigAsync(connection, transaction).WaitAsync(TestBudget);

        var zone = Assert.Single(zones);
        Assert.Equal("uncommitted", zone.Id);
        Assert.Equal("Snapshot zone", zone.Label);
        Assert.Equal(DragAction.Following, zone.Action);
        transaction.Rollback();
        var store = new PersonalFeatureStateStore(_database.Factory);
        Assert.DoesNotContain(await store.LoadDragZoneConfigAsync().WaitAsync(TestBudget),
            candidate => candidate.Id == "uncommitted");
    }
}
