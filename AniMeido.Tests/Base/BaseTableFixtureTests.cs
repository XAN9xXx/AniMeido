using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class BaseTableFixtureTests : DbTestBase
{
    [Fact]
    public async Task BaseTables_KeepTheUnmigratedSchemaRequiredByLegacyFixtures()
    {
        await CreateBaseTablesAsync();
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using (var reader = await command.ExecuteReaderAsync())
        {
            var names = new List<string>();
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));
            Assert.Equal(new[] { "cache", "config", "tracking" }, names);
        }

        var expected = new Dictionary<string, (string Name, string Type, bool NotNull, bool PrimaryKey)[]>
        {
            ["tracking"] = [("AnimeID", "INTEGER", false, true), ("Status", "INTEGER", true, false), ("UpdatedAt", "TEXT", true, false)],
            ["cache"] = [("CacheKey", "TEXT", false, true), ("Data", "TEXT", true, false), ("ExpiresAt", "TEXT", true, false)],
            ["config"] = [("Key", "TEXT", false, true), ("Value", "TEXT", true, false)],
        };
        foreach (var (table, columns) in expected)
        {
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = await command.ExecuteReaderAsync();
            var actual = new List<(string, string, bool, bool)>();
            while (await reader.ReadAsync())
                actual.Add((reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(5)));
            Assert.Equal(columns, actual);
        }
    }
}
