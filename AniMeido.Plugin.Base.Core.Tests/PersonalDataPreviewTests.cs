using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PersonalDataPreviewTests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PersonalDatabaseFixture _database = new();
    private readonly MockAppDataPaths _previewPaths;
    private SqliteConnectionFactory Factory => _database.Factory;

    public PersonalDataPreviewTests() => _previewPaths = new MockAppDataPaths(Factory.DatabasePath);
    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync()
    {
        _previewPaths.Dispose();
        return _database.DisposeAsync();
    }

    [Theory]
    [InlineData(6)]
    [InlineData(5)]
    public async Task PreviewKeepsTheOriginalPayloadWithoutImportingOrBackingUp(int schemaVersion)
    {
        await new TrackingService(Factory).SetStatusAsync(777, AnimeTrackingStatus.Watching);
        var before = await ReadTrackingAsync();
        var json = Document(schemaVersion);
        var expected = Assert.IsType<ExportData>(ExportService.Preview(json));
        var transfer = CreateService(Factory, _previewPaths);
        await Factory.StopAsync().WaitAsync(Budget);

        var actual = Assert.IsType<ExportData>(await transfer.PreviewJsonAsync(json).WaitAsync(Budget));

        Assert.Equal(JsonSerializer.Serialize(expected, JsonOptions), JsonSerializer.Serialize(actual, JsonOptions));
        Assert.Equal(schemaVersion, actual.SchemaVersion);
        Assert.Equal(new[] { 42, -9 }, actual.Tracking.Select(item => item.AnimeId));
        Assert.Equal(" original summary ", actual.P4Tables!["anime_archives"][0]["SummaryNote"]);
        Assert.Equal(" original label ", Assert.Single(actual.DragZones!).Label);
        Assert.Equal(before, await ReadTrackingAsync());
        Assert.Empty(Directory.GetFiles(_previewPaths.BackupDirectory));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"schemaVersion\":6,\"idSpace\":\"wrong\",\"tracking\":[]}")]
    [InlineData("{\"schemaVersion\":0,\"tracking\":[]}")]
    public async Task InvalidPreviewReturnsTheSameNullResultAsTheOriginalParser(string json)
    {
        var transfer = CreateService(Factory, _previewPaths);
        await Factory.StopAsync().WaitAsync(Budget);
        Assert.Null(ExportService.Preview(json));

        Assert.Null(await transfer.PreviewJsonAsync(json).WaitAsync(Budget));

        Assert.Empty(Directory.GetFiles(_previewPaths.BackupDirectory));
    }

    [Fact]
    public async Task PreCancelledPreviewDoesNotCreateADatabaseOrBackup()
    {
        var database = new PersonalDatabaseFixture();
        using var paths = new MockAppDataPaths(database.Factory.DatabasePath);
        var transfer = CreateService(database.Factory, paths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            Assert.False(File.Exists(database.Factory.DatabasePath));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer
                .PreviewJsonAsync(Document(5), cancellation.Token).WaitAsync(Budget));

            Assert.False(File.Exists(database.Factory.DatabasePath));
            Assert.Empty(Directory.GetFiles(paths.BackupDirectory));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private async Task<(int AnimeId, int Status, string UpdatedAt)[]> ReadTrackingAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Factory.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT AnimeId, Status, UpdatedAt FROM tracking ORDER BY AnimeId";
        var rows = new List<(int AnimeId, int Status, string UpdatedAt)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2)));
        return rows.ToArray();
    }

    private static PersonalDataTransferService CreateService(
        SqliteConnectionFactory factory, MockAppDataPaths paths)
    {
        var export = new ExportService(factory);
        var backup = new BackupService(factory, paths);
        var bundle = new ArchiveBundleService(export, new ArchiveService(factory), backup,
            NullLogger<ArchiveBundleService>.Instance);
        return new PersonalDataTransferService(factory, export, backup, bundle);
    }

    private static string Document(int schemaVersion) => $$"""
        {
          "schemaVersion":{{schemaVersion}},"idSpace":"animeido-public",
          "exportedAt":"2026-10-29T01:02:03+08:00","appVersion":"fixture-version",
          "tracking":[
            {"animeId":42,"status":1,"updatedAt":"2026-10-27T01:02:03+08:00"},
            {"animeId":-9,"status":4,"updatedAt":"2026-10-26T01:02:03+08:00"}],
          "legacyAnimeRefs":[{"localId":-9,"legacyBangumiId":9,"reason":"not_found",
            "titleSnapshot":" legacy title ","recordedAt":"2026-10-25T01:02:03+08:00","keptAt":null}],
          "vocabularyNames":["original vocab"],
          "dragZones":[{"id":"fixed-zone","label":" original label ","action":2,
            "xPercent":0.1,"yPercent":0.2,"widthPercent":0.3,"heightPercent":0.4}],
          "savedTags":[{"tagName":" original tag ","invalid":true}],
          "p4Tables":{"anime_archives":[{"AnimeId":"42","TitleSnapshot":" original title ",
            "PersonalRating":"8.5","SummaryNote":" original summary ","CreatedAt":"2026-01-01T01:02:03+08:00",
            "UpdatedAt":"2026-10-28T01:02:03+08:00"}]}
        }
        """;
}
