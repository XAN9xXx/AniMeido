using System.Text.Json;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed partial class PersonalIdRemapLogBatchTests
{
    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task BatchAppend_WritesLogOnceAndPreservesCompleteHistoryAndAppendOrder()
    {
        var history = BuildHistory(5);
        await ResetHistoryAsync(JsonSerializer.Serialize(history, JsonSerializerOptions.Web));
        var remap = CreateRemap();
        var notifications = 0;
        remap.Changed += () => notifications++;

        await AppendBatchAsync(remap, 3).WaitAsync(Budget);

        await AssertPublishedHistoryAsync(history, 3);
        Assert.Equal(1, notifications);
        Assert.Equal(1L, await CountLogWritesAsync());
    }

    [Fact]
    public async Task LaterBusinessFailure_RollsBackPersonalIdentityVersionAndEveryLogAddition()
    {
        var historyJson = JsonSerializer.Serialize(BuildHistory(3), JsonSerializerOptions.Web);
        await ResetHistoryAsync(historyJson);
        await SeedTrackedIdentityAsync();
        var remap = CreateRemap();
        var notifications = 0;
        remap.Changed += () => notifications++;
        var failure = new InvalidOperationException("fixture failure after log additions");

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remap.CommitIdentityChangesAsync(async (connection, transaction, log) =>
            {
                await RewriteFixtureIdentityAsync(connection, transaction);
                await log.AppendAsync(-1, 100000, CancellationToken.None);
                await log.AppendAsync(-2, 100001, CancellationToken.None);
                throw failure;
            }, CancellationToken.None).WaitAsync(Budget));

        Assert.Same(failure, observed);
        await AssertFixtureIdentityAsync(rewritten: false);
        Assert.Equal(historyJson, await ReadCommittedLogAsync());
        Assert.Equal(0L, await CountLogWritesAsync());
        Assert.Equal(0, notifications);
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public async Task LogSaveFailure_RollsBackPersonalIdentityVersionAndLogTogether()
    {
        var historyJson = JsonSerializer.Serialize(BuildHistory(3), JsonSerializerOptions.Web);
        await ResetHistoryAsync(historyJson);
        await SeedTrackedIdentityAsync();
        await ExecuteBatchFixtureSqlAsync("""
            CREATE TRIGGER fixture_reject_log BEFORE UPDATE ON config
            WHEN NEW.Key='public-id-remap-log'
            BEGIN
                SELECT RAISE(ABORT, 'fixture log save failure');
            END;
            """);
        var remap = CreateRemap();
        var notifications = 0;
        remap.Changed += () => notifications++;

        var failure = await Assert.ThrowsAsync<SqliteException>(() =>
            remap.CommitIdentityChangesAsync(async (connection, transaction, log) =>
            {
                await RewriteFixtureIdentityAsync(connection, transaction);
                await log.AppendAsync(-1, 100000, CancellationToken.None);
                await log.AppendAsync(-2, 100001, CancellationToken.None);
            }, CancellationToken.None).WaitAsync(Budget));

        Assert.Equal(19, failure.SqliteErrorCode);
        Assert.Contains("fixture log save failure", failure.Message);
        await AssertFixtureIdentityAsync(rewritten: false);
        Assert.Equal(historyJson, await ReadCommittedLogAsync());
        Assert.Equal(0L, await CountLogWritesAsync());
        Assert.Equal(0, notifications);
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public async Task PublicationFailure_CommitsOnceAndNextEmptyCommitRepublishesCompleteLog()
    {
        var history = BuildHistory(3);
        await ResetHistoryAsync(JsonSerializer.Serialize(history, JsonSerializerOptions.Web));
        await SeedTrackedIdentityAsync();
        var remap = CreateRemap();
        var notifications = 0;
        remap.Changed += () => notifications++;
        string committedJson;
        long committedWrites;
        Directory.CreateDirectory(LogPath);
        try
        {
            await remap.CommitIdentityChangesAsync(async (connection, transaction, log) =>
            {
                await RewriteFixtureIdentityAsync(connection, transaction);
                await log.AppendAsync(-1, 100000, CancellationToken.None);
            }, CancellationToken.None).WaitAsync(Budget);

            Assert.True(Directory.Exists(LogPath));
            Assert.False(File.Exists(LogPath));
            Assert.Equal(1, notifications);
            await AssertFixtureIdentityAsync(rewritten: true);
            committedJson = await ReadCommittedLogAsync();
            committedWrites = await CountLogWritesAsync();
        }
        finally
        {
            Directory.Delete(LogPath);
        }

        await remap.CommitIdentityChangesAsync((_, _, _) => Task.CompletedTask, CancellationToken.None)
            .WaitAsync(Budget);

        await AssertPublishedHistoryAsync(history, 1);
        Assert.Equal(committedJson, await ReadCommittedLogAsync());
        Assert.Equal(committedWrites, await CountLogWritesAsync());
        await AssertFixtureIdentityAsync(rewritten: true);
        Assert.Equal(2, notifications);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{fixture malformed remap history")]
    public async Task EmptyAppend_DoesNotParseOrRewriteMalformedHistoryAndPublishesItUnchanged(string historyJson)
    {
        await ResetHistoryAsync(historyJson);
        var remap = CreateRemap();
        var notifications = 0;
        remap.Changed += () => notifications++;

        await remap.CommitIdentityChangesAsync((_, _, _) => Task.CompletedTask, CancellationToken.None)
            .WaitAsync(Budget);

        Assert.Equal(historyJson, await ReadCommittedLogAsync());
        Assert.Equal(historyJson, await File.ReadAllTextAsync(LogPath));
        Assert.Equal(0L, await CountLogWritesAsync());
        Assert.Equal(1, notifications);
    }

    private async Task SeedTrackedIdentityAsync()
    {
        await new TrackingService(Factory).SetStatusAsync(-1, AnimeTrackingStatus.Following);
        await ExecuteBatchFixtureSqlAsync("""
            INSERT INTO config(Key, Value) VALUES('fixture_identity_version', 'old');
            CREATE TABLE fixture_personal_updates(AnimeId INTEGER NOT NULL);
            CREATE TRIGGER fixture_count_identity_update AFTER UPDATE ON tracking
            BEGIN
                INSERT INTO fixture_personal_updates(AnimeId) VALUES(NEW.AnimeId);
            END;
            """);
    }

    private static async Task RewriteFixtureIdentityAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tracking SET AnimeId=100000 WHERE AnimeId=-1;
            UPDATE config SET Value='new' WHERE Key='fixture_identity_version';
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task AssertFixtureIdentityAsync(bool rewritten)
    {
        var record = Assert.Single(await new TrackingService(Factory).GetAllTrackingAsync());
        Assert.Equal(rewritten ? 100000 : -1, record.AnimeId);
        Assert.Equal(AnimeTrackingStatus.Following, record.Status);
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM config WHERE Key='fixture_identity_version'";
        Assert.Equal(rewritten ? "new" : "old", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM fixture_personal_updates";
        Assert.Equal(rewritten ? 1L : 0L, await command.ExecuteScalarAsync());
    }

    private async Task<string> ReadCommittedLogAsync()
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM config WHERE Key=@key";
        command.Parameters.AddWithValue("@key", PersonalIdRemapService.LogConfigKey);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteBatchFixtureSqlAsync(string sql)
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
