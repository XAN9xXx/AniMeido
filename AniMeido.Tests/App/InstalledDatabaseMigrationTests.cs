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
            Assert.StartsWith(Path.Combine(Paths.BackupDirectory, "update", $"AniMeido-before-v7-to-v{DatabaseSchema.CurrentVersion}-"), record.Backup);
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
    public async Task RecordMigrationLockFailurePreventsChangesAndRetryRegistersBeforeMigration()
    {
        await PrepareVersionSevenAsync();
        var (context, store) = CreateInstall(installed: true, TimeSpan.FromMilliseconds(80));
        var before = File.ReadAllBytes(store.StatePath);
        using (var held = new FileStream(Path.Combine(store.Root, "state.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<InstallException>(
                () => new DatabaseService(DbFactory, Paths, context).InitializeAsync());
        }
        using var current = await DbFactory.OpenAsync();
        Assert.Equal(7, await DatabaseSchema.GetSchemaVersionAsync(current));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Null(store.Read().Migration);
        Assert.Single(Directory.GetFiles(Path.Combine(Paths.BackupDirectory, "update"), $"AniMeido-before-v7-to-v{DatabaseSchema.CurrentVersion}-*.db"));
        await new DatabaseService(DbFactory, Paths, context).InitializeAsync();
        Assert.Equal(DatabaseSchema.CurrentVersion, await DatabaseSchema.GetSchemaVersionAsync(current));
        Assert.NotNull(store.Read().Migration);
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
    public async Task BlockedAtV8RestartsKeepTheOriginalUpdateBackupAndRecord()
    {
        await PrepareVersionSevenAsync();
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            // 有个人记录时空库捷径不适用，库停在 v8 等待联网迁移。
            command.CommandText = "INSERT INTO saved_tags(TagName) VALUES('漫画改')";
            await command.ExecuteNonQueryAsync();
        }
        var (context, store) = CreateInstall(installed: true);
        await new DatabaseService(DbFactory, Paths, context).InitializeAsync();
        var original = store.Read().Migration!.Backup;
        await new DatabaseService(DbFactory, Paths, context).InitializeAsync();
        await new DatabaseService(DbFactory, Paths, context).InitializeAsync();

        using (var current = await DbFactory.OpenAsync())
            Assert.Equal(DatabaseSchema.OfflineVersion, await DatabaseSchema.GetSchemaVersionAsync(current));
        var backups = Directory.GetFiles(Path.Combine(Paths.BackupDirectory, "update"), "AniMeido-before-*.db");
        Assert.Equal(original, Assert.Single(backups));
        Assert.Contains("-before-v7-to-v", original);
        Assert.Equal(original, store.Read().Migration!.Backup);

        // 备份被删除后，下一次启动重新备份当前的 v8 库。
        File.Delete(original);
        await new DatabaseService(DbFactory, Paths, context).InitializeAsync();
        Assert.Contains("-before-v8-to-v", Assert.Single(Directory.GetFiles(Path.Combine(Paths.BackupDirectory, "update"), "AniMeido-before-*.db")));
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

    [Fact]
    public async Task ExistingBackupIsRegisteredAgainAfterPreviouslyMissingRegistration()
    {
        await PrepareVersionSevenAsync();
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO saved_tags(TagName) VALUES('keep-v8')";
            await command.ExecuteNonQueryAsync();
        }
        var (context, store) = CreateInstall(installed: true, TimeSpan.FromMilliseconds(80));
        await new DatabaseService(DbFactory, Paths, context).InitializeAsync();
        var backup = store.Read().Migration!.Backup;
        store.Write(store.Read() with { Migration = null });
        using (var held = new FileStream(Path.Combine(store.Root, "state.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<InstallException>(
                () => new DatabaseService(DbFactory, Paths, context).InitializeAsync());
        }
        Assert.Null(store.Read().Migration);
        await new DatabaseService(DbFactory, Paths, context).InitializeAsync();
        Assert.Equal(backup, store.Read().Migration!.Backup);
        Assert.Single(Directory.GetFiles(Path.Combine(Paths.BackupDirectory, "update"), "*.db"));
    }

    [Fact]
    public async Task UpdateBackupContainsOriginalTablesBeforeAnySchemaPreparation()
    {
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE original(Value TEXT); PRAGMA user_version=1;";
            await command.ExecuteNonQueryAsync();
        }
        var (context, store) = CreateInstall(installed: true);
        await new DatabaseService(DbFactory, Paths, context).InitializeAsync();
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.Read().Migration!.Backup,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await backup.OpenAsync();
        using var query = backup.CreateCommand();
        query.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        Assert.Equal("original", await query.ExecuteScalarAsync());
        using var rows = await query.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.False(await rows.ReadAsync());
    }

    [Fact]
    public async Task InvalidRecordedBackupDoesNotTriggerAutomaticDatabaseReplacement()
    {
        await PrepareVersionSevenAsync();
        var badBackup = Path.Combine(Paths.RootDirectory, "bad-backup.db");
        await File.WriteAllTextAsync(badBackup, "not SQLite");
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA user_version=8;
                INSERT INTO saved_tags(TagName) VALUES('preserved');
                INSERT INTO config(Key,Value) VALUES('public-id-migration-backup', @path);
                """;
            command.Parameters.AddWithValue("@path", badBackup);
            await command.ExecuteNonQueryAsync();
        }
        var (context, store) = CreateInstall(installed: true);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => new DatabaseService(DbFactory, Paths, context).InitializeAsync());
        Assert.Null(store.Read().Migration);
        Assert.Equal("preserved", Assert.Single(await new SavedTagService(DbFactory).GetAllSavedTagsAsync()));
    }

    [Fact]
    public async Task DifferentCurrentVersionCannotSilentlySkipMigrationRegistration()
    {
        await PrepareVersionSevenAsync();
        var (context, store) = CreateInstall(installed: true);
        var directory = InstallLayout.VersionDirectory(store.Root, "2.4.0");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, InstallLayout.AppFileName), "never executed");
        store.Write(store.Read() with { Current = "2.4.0" });

        await Assert.ThrowsAsync<InstallException>(
            () => new DatabaseService(DbFactory, Paths, context).InitializeAsync());
        using var connection = await DbFactory.OpenAsync();
        Assert.Equal(7, await DatabaseSchema.GetSchemaVersionAsync(connection));
        Assert.Null(store.Read().Migration);
    }

    private async Task PrepareVersionSevenAsync()
    {
        using var connection = await DbFactory.OpenAsync();
        await DatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE config(Key TEXT PRIMARY KEY,Value TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync();
        await DatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
        await DatabaseSchema.RunMigrationsAsync(connection);
        // v8 只修复已有数据；无数据时将离线 schema 标回 v7，不混入 v9 的表与列。
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
