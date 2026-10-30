using System.Text.Json;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed class PublicIdCheckLogBatchTests : DbTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Mechanism")]
    public async Task Check_MultipleRewritesShareOneLogSaveAndCommitOrRollbackTogether(bool rejectLogSave)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        foreach (var id in new[] { 1, 2, 3 })
            await tracking.SetStatusAsync(id, AnimeTrackingStatus.Following);

        const string history = """[{"seq":1,"from":7,"to":8,"at":"2026-10-20T00:00:00Z"}]""";
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO config(Key,Value) VALUES('public-id-remap-log',@history);
                CREATE TABLE fixture_log_writes(Id INTEGER NOT NULL);
                CREATE TRIGGER count_log_write AFTER UPDATE ON config
                WHEN NEW.Key='public-id-remap-log'
                BEGIN INSERT INTO fixture_log_writes(Id) VALUES(1); END;
                """;
            command.Parameters.AddWithValue("@history", history);
            await command.ExecuteNonQueryAsync();
            if (rejectLogSave)
            {
                command.CommandText = """
                    CREATE TRIGGER reject_log_write BEFORE UPDATE ON config
                    WHEN NEW.Key='public-id-remap-log'
                    BEGIN SELECT RAISE(ABORT, 'fixture rejected batch log'); END;
                    """;
                await command.ExecuteNonQueryAsync();
            }
        }

        using var handler = new PendingReviewTests.CheckHandler { Work = id => ("merged", id + 100) };
        using var http = handler.Client();
        var remap = new PersonalIdRemapService(DbFactory, NullLogger<PersonalIdRemapService>.Instance);
        var metadata = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, http, new CacheService(DbFactory));
        await using var check = new PublicIdCheckService(DbFactory, http, metadata, remap,
            NullLogger<PublicIdCheckService>.Instance);
        var notifications = 0;
        remap.Changed += () => notifications++;

        if (rejectLogSave)
        {
            var error = await Assert.ThrowsAsync<SqliteException>(() =>
                check.CheckAsync("v1").WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("fixture rejected batch log", error.Message, StringComparison.Ordinal);
            Assert.Equal(new[] { 1, 2, 3 }, (await tracking.GetAllTrackingAsync()).Select(row => row.AnimeId).Order());
            Assert.Null(await ScalarAsync("SELECT Value FROM config WHERE Key='public-id-check-version'"));
            Assert.Equal(history, await ScalarAsync("SELECT Value FROM config WHERE Key='public-id-remap-log'"));
            Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM fixture_log_writes"));
            Assert.Equal(0, notifications);
            Assert.False(File.Exists(Path.Combine(Paths.RootDirectory, "id-remap-log.json")));
            return;
        }

        await check.CheckAsync("v1").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { 101, 102, 103 }, (await tracking.GetAllTrackingAsync()).Select(row => row.AnimeId).Order());
        Assert.Equal("v1", await ScalarAsync("SELECT Value FROM config WHERE Key='public-id-check-version'"));
        Assert.Equal(1, notifications);
        var json = Assert.IsType<string>(await ScalarAsync("SELECT Value FROM config WHERE Key='public-id-remap-log'"));
        Assert.Equal(json, await File.ReadAllTextAsync(Path.Combine(Paths.RootDirectory, "id-remap-log.json")));
        using var log = JsonDocument.Parse(json);
        var rows = log.RootElement.EnumerateArray().ToArray();
        Assert.Equal(new long[] { 1, 2, 3, 4 }, rows.Select(row => row.GetProperty("seq").GetInt64()));
        Assert.Equal(new[] { 7, 1, 2, 3 }, rows.Select(row => row.GetProperty("from").GetInt32()));
        Assert.Equal(new[] { 8, 101, 102, 103 }, rows.Select(row => row.GetProperty("to").GetInt32()));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM fixture_log_writes"));
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
