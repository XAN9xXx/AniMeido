using AniMeido.App.Services;
using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using AniMeido.Updates.Install;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Tests;

public sealed class InstalledDatabaseMigrationTests : DbTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordsMigrationOnlyInInstalledModeAndBackupContainsOldSchema(bool installed)
    {
        await PrepareVersionSevenAsync();
        var (context, store) = CreateInstall(installed);
        await new DatabaseService(DbFactory, Paths, context).InitializeAsync();
        using var current = await DbFactory.OpenAsync();
        Assert.Equal(DatabaseSchema.CurrentVersion, await DatabaseSchema.GetSchemaVersionAsync(current));
        var record = store.Read().Migration;
        if (installed)
        {
            Assert.NotNull(record);
            Assert.Equal("2.3.0", record.Version);
            Assert.StartsWith(Path.Combine(Paths.BackupDirectory, "update", "AniMeido-before-v7-to-v8-"), record.Backup);
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = record.Backup,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await backup.OpenAsync();
            Assert.Equal(7, await DatabaseSchema.GetSchemaVersionAsync(backup));
        }
        else
        {
            Assert.Null(record);
        }
    }

    [Fact]
    public async Task RecordMigrationLockFailureDoesNotPreventSchemaMigration()
    {
        await PrepareVersionSevenAsync();
        var (context, store) = CreateInstall(installed: true, TimeSpan.FromMilliseconds(80));
        var before = File.ReadAllBytes(store.StatePath);
        using (var held = new FileStream(Path.Combine(store.Root, "state.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            await new DatabaseService(DbFactory, Paths, context).InitializeAsync();
        }
        using var current = await DbFactory.OpenAsync();
        Assert.Equal(DatabaseSchema.CurrentVersion, await DatabaseSchema.GetSchemaVersionAsync(current));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Null(store.Read().Migration);
        Assert.Single(Directory.GetFiles(Path.Combine(Paths.BackupDirectory, "update"), "AniMeido-before-v7-to-v8-*.db"));
    }

    [Fact]
    public async Task MigrationRecordExistsEvenWhenSubsequentSqlMigrationFails()
    {
        await PrepareVersionSevenAsync();
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            // v8 会 UPDATE anime_plans；破坏该表结构，验证 RecordMigration 必须在 SQL 之前完成。
            command.CommandText = "DROP TABLE anime_plans; CREATE TABLE anime_plans(AnimeId INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }
        var (context, store) = CreateInstall(installed: true);
        await Assert.ThrowsAsync<SqliteException>(() => new DatabaseService(DbFactory, Paths, context).InitializeAsync());
        Assert.NotNull(store.Read().Migration);
        Assert.True(File.Exists(store.Read().Migration!.Backup));
    }

    [Fact]
    public async Task FreshDatabaseDoesNotRecordAMigration()
    {
        var (context, store) = CreateInstall(installed: true);
        await new DatabaseService(DbFactory, Paths, context).InitializeAsync();
        Assert.Null(store.Read().Migration);
    }

    [Fact]
    public async Task DependencyInjectionSelectsOptionalInstalledContextConstructor()
    {
        await PrepareVersionSevenAsync();
        var (context, store) = CreateInstall(installed: true);
        using var provider = new ServiceCollection()
            .AddSingleton<IAppDataPaths>(Paths)
            .AddSingleton(DbFactory)
            .AddSingleton(context)
            .AddSingleton<DatabaseService>()
            .BuildServiceProvider();
        await provider.GetRequiredService<DatabaseService>().InitializeAsync();
        Assert.Equal("2.3.0", store.Read().Migration!.Version);
    }

    private async Task PrepareVersionSevenAsync()
    {
        await new DatabaseService(DbFactory, Paths).InitializeAsync();
        // InitializeAsync 的普通备份同步使用 SQLite BackupDatabase；这里只修改 schema 版本模拟升级。
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version = 7;";
        await command.ExecuteNonQueryAsync();
    }

    private (InstalledAppContext Context, InstallStateStore Store) CreateInstall(bool installed, TimeSpan? timeout = null)
    {
        var root = Path.Combine(Paths.RootDirectory, "install");
        var directory = InstallLayout.VersionDirectory(root, "2.3.0");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, InstallLayout.AppFileName);
        File.WriteAllText(executable, "test-only, never executed");
        var store = new InstallStateStore(root);
        store.Write(new InstallState { Current = "2.3.0", Previous = "2.2.0", CurrentHealthy = false, FailedStarts = 1 });
        return (new InstalledAppContext(installed ? executable : Path.Combine(root, "debug", InstallLayout.AppFileName), timeout), store);
    }
}
