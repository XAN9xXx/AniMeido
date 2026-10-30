using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed class PersonalDataTransferServiceTests : DbTestBase
{
    [Fact]
    public async Task JsonImport_BackupContainsStateImmediatelyBeforeCommit()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var transfer = CreateService();
        await tracking.SetStatusAsync(42, AnimeTrackingStatus.Watching);
        var json = await transfer.ExportJsonAsync();
        await tracking.SetStatusAsync(42, AnimeTrackingStatus.Completed);

        var before = Directory.GetFiles(Paths.BackupDirectory, "AniMeido-*.db");
        var result = await transfer.ImportJsonAsync(json);

        Assert.Equal(1, result.trackingCount);
        var backup = Assert.Single(Directory.GetFiles(Paths.BackupDirectory, "AniMeido-*.db").Except(before));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT Status FROM tracking WHERE AnimeId = 42";
        Assert.Equal((int)AnimeTrackingStatus.Completed, Convert.ToInt32(await query.ExecuteScalarAsync()));
        Assert.Equal(AnimeTrackingStatus.Watching, await tracking.GetStatusAsync(42));
    }

    [Fact]
    public async Task InvalidJson_DoesNotCreateBackupOrChangeRecords()
    {
        await RunProductionMigrationAsync();
        var transfer = CreateService();
        var before = Directory.GetFiles(Paths.BackupDirectory, "AniMeido-*.db");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => transfer.ImportJsonAsync("not JSON"));
        Assert.Equal(before, Directory.GetFiles(Paths.BackupDirectory, "AniMeido-*.db"));
    }

    [Fact]
    public async Task JsonImport_DoesNotBypassAnotherExclusiveOperation()
    {
        await RunProductionMigrationAsync();
        var transfer = CreateService();
        var json = await transfer.ExportJsonAsync();
        var before = Directory.GetFiles(Paths.BackupDirectory, "AniMeido-*.db");
        using var exclusive = DbFactory.Access.BeginExclusive();
        await exclusive.WaitForDrainAsync(CancellationToken.None);
        Task import;
        using (ExecutionContext.SuppressFlow())
            import = transfer.ImportJsonAsync(json);

        await Assert.ThrowsAsync<PersonalDataBusyException>(() => import);
        Assert.Equal(before, Directory.GetFiles(Paths.BackupDirectory, "AniMeido-*.db"));
    }

    private PersonalDataTransferService CreateService()
    {
        var export = new ExportService(DbFactory);
        var backup = new BackupService(DbFactory, Paths);
        var bundle = new ArchiveBundleService(
            export, new ArchiveService(DbFactory), backup, NullLogger<ArchiveBundleService>.Instance);
        return new PersonalDataTransferService(DbFactory, export, backup, bundle);
    }
}
