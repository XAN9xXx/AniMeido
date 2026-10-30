using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class PersonalDatabaseSchemaTests
{
    [Fact]
    public async Task EmptyDatabase_InitializesAndUpgradesWithoutDesktopServices()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await PersonalDatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE config(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync();
        }
        await PersonalDatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
        await PersonalDatabaseSchema.RunMigrationsAsync(connection);
        Assert.Equal(PersonalDatabaseSchema.OfflineVersion,
            await PersonalDatabaseSchema.GetSchemaVersionAsync(connection));

        Assert.True(await PersonalDatabaseSchema.UpgradeEmptyDatabaseAsync(connection));
        Assert.Equal(PersonalDatabaseSchema.CurrentVersion,
            await PersonalDatabaseSchema.GetSchemaVersionAsync(connection));
        Assert.False(await PersonalDatabaseSchema.UpgradeEmptyDatabaseAsync(connection));
    }
}
