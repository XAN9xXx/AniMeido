using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class FixtureCleanupTests
{
    [Fact]
    public async Task Dispose_RemovesOwnedDatabaseSidecarsBackupsLogsAndAdditionalRoots()
    {
        var fixture = new Fixture();
        var roots = fixture.Roots;
        try
        {
            await fixture.SeedAsync();
            Assert.All(roots, root => Assert.True(Directory.Exists(root)));
        }
        finally { fixture.Dispose(); }

        Assert.All(roots, root => Assert.False(Directory.Exists(root)));
    }

    [Fact]
    public void Dispose_WithCallerDatabaseDoesNotDeleteCallerFileOrDirectory()
    {
        using var caller = new MockAppDataPaths();
        File.WriteAllText(caller.DatabasePath, "caller-owned");
        string ownedRoot;
        using (var paths = new MockAppDataPaths(caller.DatabasePath))
        {
            ownedRoot = paths.RootDirectory;
            Assert.NotEqual(caller.RootDirectory, ownedRoot);
            Directory.CreateDirectory(paths.BackupDirectory);
            File.WriteAllText(Path.Combine(paths.BackupDirectory, "backup.db"), "backup");
        }

        Assert.False(Directory.Exists(ownedRoot));
        Assert.True(Directory.Exists(caller.RootDirectory));
        Assert.Equal("caller-owned", File.ReadAllText(caller.DatabasePath));
    }

    private sealed class Fixture : DbTestBase
    {
        private readonly MockAppDataPaths _extra;
        public Fixture() => _extra = CreateAdditionalPaths();
        public string[] Roots => [Paths.RootDirectory, _extra.RootDirectory];

        public async Task SeedAsync()
        {
            await CreateBaseTablesAsync();
            using (var connection = await new SqliteConnectionFactory(_extra).OpenAsync())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE sample(Value TEXT)";
                await command.ExecuteNonQueryAsync();
            }
            foreach (var paths in new[] { Paths, _extra })
            {
                Directory.CreateDirectory(paths.BackupDirectory);
                Directory.CreateDirectory(paths.LogDirectory);
                File.WriteAllText(Path.Combine(paths.BackupDirectory, "backup.db"), "backup");
                File.WriteAllText(Path.Combine(paths.LogDirectory, "log.txt"), "log");
                File.WriteAllText(paths.DatabasePath + "-wal", "sidecar");
                File.WriteAllText(paths.DatabasePath + "-shm", "sidecar");
            }
        }
    }
}
