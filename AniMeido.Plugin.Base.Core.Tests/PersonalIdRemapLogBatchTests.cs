using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed partial class PersonalIdRemapLogBatchTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;
    private string LogPath => Path.Combine(Path.GetDirectoryName(Factory.DatabasePath)!, "id-remap-log.json");

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE fixture_log_writes(Operation TEXT NOT NULL);
            CREATE TRIGGER fixture_count_log_insert AFTER INSERT ON config
            WHEN NEW.Key='public-id-remap-log'
            BEGIN
                INSERT INTO fixture_log_writes(Operation) VALUES('insert');
            END;
            CREATE TRIGGER fixture_count_log_update AFTER UPDATE ON config
            WHEN NEW.Key='public-id-remap-log'
            BEGIN
                INSERT INTO fixture_log_writes(Operation) VALUES('update');
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(1000, 20)]
    [InlineData(5000, 100)]
    [Trait("Category", "Performance")]
    public async Task IdentityLogBatch_PreservesCompleteOrderedHistoryAndMeasuresCommit(int historyCount, int additions)
    {
        var history = BuildHistory(historyCount);
        var historyJson = JsonSerializer.Serialize(history, JsonSerializerOptions.Web);
        var remap = CreateRemap();
        var notifications = 0;
        remap.Changed += () => notifications++;
        for (var warmup = 0; warmup < 3; warmup++)
        {
            await ResetHistoryAsync(historyJson);
            notifications = 0;
            await AppendBatchAsync(remap, additions).WaitAsync(Budget);
            await AssertPublishedHistoryAsync(history, additions);
            Assert.Equal(1, notifications);
        }

        var milliseconds = new double[5];
        var allocated = new long[5];
        var writes = new long[5];
        for (var sample = 0; sample < milliseconds.Length; sample++)
        {
            await ResetHistoryAsync(historyJson);
            notifications = 0;
            var beforeBytes = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            await AppendBatchAsync(remap, additions).WaitAsync(Budget);
            milliseconds[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated[sample] = GC.GetTotalAllocatedBytes(precise: true) - beforeBytes;

            await AssertPublishedHistoryAsync(history, additions);
            Assert.Equal(1, notifications);
            writes[sample] = await CountLogWritesAsync();
        }

        output.WriteLine("History={0}; additions={1}; 3 warmups / 5 samples; same synthetic history reset before every commit.",
            historyCount, additions);
        output.WriteLine("MedianCommitMs={0:F3}; MedianProcessAllocatedBytes={1}; MedianLogWrites={2}",
            milliseconds.Order().ElementAt(2), allocated.Order().ElementAt(2), writes.Order().ElementAt(2));
        output.WriteLine("Samples ms=[{0}]; bytes=[{1}]; config log writes=[{2}]",
            string.Join(", ", milliseconds.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))),
            string.Join(", ", allocated), string.Join(", ", writes));
        output.WriteLine("Timing includes production commit, complete log publication and notification, plus per-write counting triggers.");
        output.WriteLine("History setup and assertions excluded; no personal-record rewrite or network; process allocation requires isolated class execution.");
    }

    private PersonalIdRemapService CreateRemap()
        => new(Factory, NullLogger<PersonalIdRemapService>.Instance);

    private static List<IdRemapEntry> BuildHistory(int count)
        => Enumerable.Range(1, count)
            .Select(index => new IdRemapEntry(index, index, index + 1, "2026-10-01T00:00:00.0000000+00:00"))
            .ToList();

    private static Task AppendBatchAsync(PersonalIdRemapService remap, int additions)
        => remap.CommitIdentityChangesAsync(async (_, _, log) =>
        {
            for (var index = 0; index < additions; index++)
            {
                await log.AppendAsync(-(index + 1), 100000 + index, CancellationToken.None);
            }
        }, CancellationToken.None);

    private async Task ResetHistoryAsync(string json)
    {
        using var connection = await Factory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO config(Key, Value) VALUES(@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
            DELETE FROM fixture_log_writes;
            """;
        command.Parameters.AddWithValue("@key", PersonalIdRemapService.LogConfigKey);
        command.Parameters.AddWithValue("@value", json);
        await command.ExecuteNonQueryAsync();
        transaction.Commit();
    }

    private async Task AssertPublishedHistoryAsync(IReadOnlyList<IdRemapEntry> history, int additions)
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM config WHERE Key=@key";
        command.Parameters.AddWithValue("@key", PersonalIdRemapService.LogConfigKey);
        var json = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(LogPath));
        var rows = JsonSerializer.Deserialize<List<IdRemapEntry>>(json, JsonSerializerOptions.Web)!;
        Assert.Equal(history.Count + additions, rows.Count);
        Assert.Equal(history, rows.Take(history.Count));
        Assert.Equal(Enumerable.Range(1, rows.Count).Select(value => (long)value), rows.Select(row => row.Seq));
        for (var index = 0; index < additions; index++)
        {
            var row = rows[history.Count + index];
            Assert.Equal(-(index + 1), row.From);
            Assert.Equal(100000 + index, row.To);
            Assert.True(DateTimeOffset.TryParse(row.At, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        }
    }

    private async Task<long> CountLogWritesAsync()
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fixture_log_writes";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
