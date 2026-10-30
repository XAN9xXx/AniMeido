using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class TrackingServiceSchedulingTests
{
    public enum ConfigurationSaveKind
    {
        DragZones,
        CalendarPick,
        TimeMachine,
        TodayTheme,
        RecentSearches,
    }

    [Theory]
    [InlineData(ConfigurationSaveKind.DragZones)]
    [InlineData(ConfigurationSaveKind.CalendarPick)]
    [InlineData(ConfigurationSaveKind.TimeMachine)]
    [InlineData(ConfigurationSaveKind.TodayTheme)]
    [InlineData(ConfigurationSaveKind.RecentSearches)]
    public async Task SaveConfiguration_ReturnsWhileSqliteIsLocked_AndKeepsTheCallTimeSnapshot(
        ConfigurationSaveKind kind)
    {
        var stateStore = _stateStore;
        var fixture = CreateConfigurationWriteCase(stateStore, kind);
        using var blocker = await LockDatabaseAsync();
        var context = new PausedUiContext();
        var invocation = CallFromPausedContextThread(() =>
        {
            var holder = fixture.Save();
            var target = fixture.Save();
            return Task.WhenAll(holder, target);
        }, context);
        Task operation;
        try
        {
            operation = await invocation.Returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);
            fixture.Mutate();
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            await FinishInvocationAsync(invocation);
        }

        await operation;
        await fixture.Verify();
    }

    [Fact]
    public async Task QueuedConfigurationSaves_KeepTheLastSubmittedValueForTheSameKey()
    {
        var stateStore = _stateStore;
        using var blocker = await LockDatabaseAsync();
        var batch = QueueRecentSearchSaves(stateStore, ["first"], ["second"], ["last"]);
        Task operation;
        try
        {
            operation = await batch.Invocation.Returned.WaitAsync(ReturnBudget);
            Assert.All(await batch.Writes, write => Assert.False(write.IsCompleted));
        }
        finally
        {
            blocker.Dispose();
            batch.Context.Resume();
            await FinishInvocationAsync(batch.Invocation);
        }

        await operation;
        Assert.Equal(new[] { "last" }, await stateStore.LoadRecentSearchesAsync());
    }

    [Fact]
    public async Task QueuedConfigurationSaves_HoldAdmissionWhileAnImportWaits_AndFinishBeforeItsDrain()
    {
        var stateStore = _stateStore;
        using var blocker = await LockDatabaseAsync();
        var batch = QueueRecentSearchSaves(stateStore, ["first"], ["second"], ["last"]);
        try
        {
            var operation = await batch.Invocation.Returned.WaitAsync(ReturnBudget);
            using var import = _dbFactory.Access.BeginExclusive();
            var drained = import.WaitForDrainAsync(CancellationToken.None);

            Assert.False(drained.IsCompleted);

            blocker.Dispose();
            batch.Context.Resume();
            await operation.WaitAsync(CompletionBudget);
            await drained.WaitAsync(CompletionBudget);

            Assert.Equal(new[] { "last" }, await stateStore.LoadRecentSearchesAsync());
        }
        finally
        {
            blocker.Dispose();
            batch.Context.Resume();
            await FinishInvocationAsync(batch.Invocation);
        }
    }

    [Fact]
    public async Task QueuedConfigurationSaves_HoldAdmissionThroughStop_AndCanFinishAfterNewRootsAreRejected()
    {
        var stateStore = _stateStore;
        using var blocker = await LockDatabaseAsync();
        var batch = QueueRecentSearchSaves(stateStore, ["first"], ["second"], ["last"]);
        Task? stopping = null;
        try
        {
            var operation = await batch.Invocation.Returned.WaitAsync(ReturnBudget);
            stopping = _dbFactory.StopAsync();

            Assert.False(stopping.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => stateStore.SaveRecentSearchesAsync(["rejected"]));

            blocker.Dispose();
            batch.Context.Resume();
            await operation.WaitAsync(CompletionBudget);
            await stopping.WaitAsync(CompletionBudget);
        }
        finally
        {
            blocker.Dispose();
            batch.Context.Resume();
            await FinishInvocationAsync(batch.Invocation);
            if (stopping is not null)
            {
                await stopping.WaitAsync(CompletionBudget);
            }
        }

        // 工厂已永久停止；这里只用本夹具的独立原生连接核对最终落盘结果。
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbFactory.DatabasePath,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM config WHERE Key='search_recent'";
        var json = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.Equal(new[] { "last" }, JsonSerializer.Deserialize<string[]>(json));
    }

    [Fact]
    public async Task QueuedConfigurationSaves_FirstSqlFailureDoesNotBlockTheNextSave()
    {
        using (var connection = await _dbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_fixture_config BEFORE INSERT ON config
                WHEN NEW.Key='search_recent' AND NEW.Value='["fail"]'
                BEGIN SELECT RAISE(ABORT, 'fixture configuration failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var stateStore = _stateStore;
        using var blocker = await LockDatabaseAsync();
        var batch = QueueRecentSearchSaves(stateStore, ["fail"], ["next"]);
        Task[] writes;
        try
        {
            await batch.Invocation.Returned.WaitAsync(ReturnBudget);
            writes = await batch.Writes;
            Assert.All(writes, write => Assert.False(write.IsCompleted));
        }
        finally
        {
            blocker.Dispose();
            batch.Context.Resume();
            await FinishInvocationAsync(batch.Invocation);
        }

        var failure = await Assert.ThrowsAsync<SqliteException>(() => writes[0]);
        Assert.Contains("fixture configuration failure", failure.Message);
        await writes[1];
        Assert.Equal(new[] { "next" }, await stateStore.LoadRecentSearchesAsync());
        using var import = _dbFactory.Access.BeginExclusive();
        Assert.True(import.WaitForDrainAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    private static (ContextInvocation Invocation, PausedUiContext Context, Task<Task[]> Writes)
        QueueRecentSearchSaves(PersonalFeatureStateStore stateStore, params string[][] values)
    {
        var submitted = new TaskCompletionSource<Task[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new PausedUiContext();
        var invocation = CallFromPausedContextThread(() =>
        {
            var writes = values.Select(value => stateStore.SaveRecentSearchesAsync(value)).ToArray();
            submitted.SetResult(writes);
            return Task.WhenAll(writes);
        }, context);
        return (invocation, context, submitted.Task);
    }

    private static ConfigurationWriteCase CreateConfigurationWriteCase(
        PersonalFeatureStateStore stateStore, ConfigurationSaveKind kind)
    {
        switch (kind)
        {
            case ConfigurationSaveKind.DragZones:
            {
                var zone = new DragZoneConfig
                {
                    Id = "original", Label = "Original", XPercent = 0.12, YPercent = 0.34,
                    WidthPercent = 0.56, HeightPercent = 0.67, Action = DragAction.Following,
                };
                var zones = new List<DragZoneConfig> { zone };
                return new ConfigurationWriteCase(
                    () => stateStore.SaveDragZoneConfigAsync(zones),
                    () =>
                    {
                        zone.Label = "Changed";
                        zone.Action = DragAction.Blocked;
                        zones.Clear();
                    },
                    async () =>
                    {
                        var actual = Assert.Single(await stateStore.LoadDragZoneConfigAsync());
                        Assert.Equal(("original", "Original", 0.12, 0.34, 0.56, 0.67, DragAction.Following),
                            (actual.Id, actual.Label, actual.XPercent, actual.YPercent,
                                actual.WidthPercent, actual.HeightPercent, actual.Action));
                    });
            }
            case ConfigurationSaveKind.CalendarPick:
            {
                var date = new DateOnly(2026, 10, 26);
                var animeId = 7;
                return new ConfigurationWriteCase(
                    () => stateStore.SaveCalendarDailyPickAsync(date, animeId),
                    () =>
                    {
                        date = date.AddDays(1);
                        animeId = 99;
                    },
                    async () =>
                    {
                        (DateOnly Date, int AnimeId)? expected = (new DateOnly(2026, 10, 26), 7);
                        Assert.Equal(expected, await stateStore.LoadCalendarDailyPickAsync());
                    });
            }
            case ConfigurationSaveKind.TimeMachine:
            {
                var ids = new List<int> { 4, 2 };
                var shown = new Dictionary<(int Year, Season Season), IReadOnlyList<int>>
                {
                    [(2024, Season.Winter)] = ids,
                };
                return new ConfigurationWriteCase(
                    () => stateStore.SaveTimeMachineShownAsync(shown),
                    () =>
                    {
                        ids.Add(99);
                        shown.Clear();
                        shown[(2025, Season.Summer)] = [8];
                    },
                    async () =>
                    {
                        var actual = await stateStore.LoadTimeMachineShownAsync();
                        Assert.Single(actual);
                        Assert.Equal(new[] { 4, 2 }, actual[(2024, Season.Winter)]);
                    });
            }
            case ConfigurationSaveKind.TodayTheme:
            {
                var ids = new List<int> { 5, 3, 5 };
                var state = new TodayThemeState(
                    new DateOnly(2026, 10, 26), TodayThemeKind.OneYearAgoToday, true, ids);
                return new ConfigurationWriteCase(
                    () => stateStore.SaveTodayThemeAsync(state),
                    () =>
                    {
                        ids.Clear();
                        ids.Add(99);
                    },
                    async () =>
                    {
                        var actual = await stateStore.LoadTodayThemeAsync();
                        Assert.NotNull(actual);
                        Assert.Equal(new DateOnly(2026, 10, 26), actual.Date);
                        Assert.Equal(TodayThemeKind.OneYearAgoToday, actual.Theme);
                        Assert.True(actual.IsFallback);
                        Assert.Equal(new[] { 5, 3, 5 }, actual.AnimeIds);
                    });
            }
            case ConfigurationSaveKind.RecentSearches:
            {
                var queries = new List<string> { "new", "old", "new" };
                return new ConfigurationWriteCase(
                    () => stateStore.SaveRecentSearchesAsync(queries),
                    () =>
                    {
                        queries.Clear();
                        queries.Add("changed");
                    },
                    async () => Assert.Equal(new[] { "new", "old", "new" },
                        await stateStore.LoadRecentSearchesAsync()));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private sealed record ConfigurationWriteCase(Func<Task> Save, Action Mutate, Func<Task> Verify);
}
