using Microsoft.Data.Sqlite;
using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;
using AniMeido.Updates.Install;

namespace AniMeido.Tests;

public sealed class InstalledRestoreTests
{
    [Fact]
    public async Task ShutdownWaitsForAcceptedRestorePreparationAndRejectsNewWork()
    {
        using var fixture = new Fixture();
        var source = fixture.MakeSource(DatabaseSchema.CurrentVersion);
        Task<string>? preparation = null;
        Task? shutdown = null;
        try
        {
            using (var held = new FileStream(Path.Combine(fixture.Root, "state.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                preparation = fixture.Context.ValidateRestoreAsync(fixture.Data, source);
                shutdown = fixture.Context.StopRestorePreparationAsync();
                Assert.False(preparation.IsCompleted);
                Assert.False(shutdown.IsCompleted);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    fixture.Context.RequestRestoreAsync(fixture.Data, source));
            }

            Assert.Equal(source, await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(fixture.Store.Read().PendingRestore);
        }
        finally
        {
            var operations = Task.WhenAll(new Task?[] { preparation, shutdown }.OfType<Task>());
            await Task.WhenAny(operations).WaitAsync(TimeSpan.FromSeconds(5));
            await operations.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task AppChecksActualSchemaBeforeWritingRequestAndDoesNotReplaceDatabase()
    {
        using var fixture = new Fixture();
        var source = fixture.MakeSource(DatabaseSchema.CurrentVersion + 1);
        File.WriteAllText(fixture.Paths.DatabasePath, "current synthetic database");
        var rejected = await Assert.ThrowsAsync<InstallException>(() =>
            fixture.Context.ValidateRestoreAsync(fixture.Data, source));
        Assert.Contains("请先升级", rejected.Message);
        await Assert.ThrowsAsync<InstallException>(() => fixture.Context.RequestRestoreAsync(fixture.Data, source));
        Assert.Null(fixture.Store.Read().PendingRestore);
        fixture.MakeSource(DatabaseSchema.CurrentVersion);
        Assert.Equal(source, await fixture.Context.ValidateRestoreAsync(fixture.Data, source));
        await fixture.Context.RequestRestoreAsync(fixture.Data, source);
        Assert.Equal(source, fixture.Store.Read().PendingRestore!.Directory);
        Assert.Equal("current synthetic database", File.ReadAllText(fixture.Paths.DatabasePath));
    }

    [Fact]
    public async Task ConfirmationDelayCannotBypassRevalidationOrUpdateMutualExclusion()
    {
        using var fixture = new Fixture();
        var source = fixture.MakeSource(DatabaseSchema.CurrentVersion);
        await fixture.Context.ValidateRestoreAsync(fixture.Data, source);
        fixture.MakeSource(DatabaseSchema.CurrentVersion + 1);
        await Assert.ThrowsAsync<InstallException>(() => fixture.Context.RequestRestoreAsync(fixture.Data, source));
        fixture.MakeSource(DatabaseSchema.CurrentVersion);
        var next = InstallLayout.VersionDirectory(fixture.Root, "2.3.0");
        Directory.CreateDirectory(next);
        File.WriteAllText(Path.Combine(next, InstallLayout.AppFileName), "never executed");
        fixture.Store.Write(fixture.Store.Read() with { Pending = "2.3.0" });
        await Assert.ThrowsAsync<InstallException>(() => fixture.Context.ValidateRestoreAsync(fixture.Data, source));
        await Assert.ThrowsAsync<InstallException>(() => fixture.Context.RequestRestoreAsync(fixture.Data, source));
        Assert.Null(fixture.Store.Read().PendingRestore);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestoreResultIsClearedOnlyAfterSuccessfulDisplayAndShownOnce(bool success)
    {
        using var fixture = new Fixture();
        var notice = new RestoreNotice
        {
            Directory = fixture.MakeSource(8), Archive = Path.Combine(fixture.Data, "archive"), Success = success,
            Error = success ? null : "synthetic failure", At = DateTimeOffset.UtcNow,
        };
        fixture.Store.Write(fixture.Store.Read() with { LastRestore = notice });
        Assert.Throws<IOException>(() => fixture.Context.ShowRestoreNotice(_ => throw new IOException("dialog failed")));
        Assert.Equal(notice, fixture.Store.Read().LastRestore);
        var shown = 0;
        fixture.Context.ShowRestoreNotice(actual =>
        {
            Assert.Equal(notice, actual);
            Assert.Equal(notice, fixture.Store.Read().LastRestore);
            shown++;
        });
        fixture.Context.ShowRestoreNotice(_ => shown++);
        Assert.Equal(1, shown);
        Assert.Null(fixture.Store.Read().LastRestore);
    }

    [Fact]
    public async Task PortableModeCannotRequestRestoreAndHasNoRestoreNotice()
    {
        using var paths = new MockAppDataPaths();
        var context = new InstalledAppContext(Path.Combine(paths.RootDirectory, "debug", InstallLayout.AppFileName));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.ValidateRestoreAsync(paths.RootDirectory, paths.RootDirectory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.RequestRestoreAsync(paths.RootDirectory, paths.RootDirectory));
        context.ShowRestoreNotice(_ => throw new InvalidOperationException("must not show"));
        Assert.Empty(Directory.GetFileSystemEntries(paths.RootDirectory));
    }

    private sealed class Fixture : IDisposable
    {
        public MockAppDataPaths Paths { get; } = new();
        public string Data => Paths.RootDirectory;
        public string Root { get; }
        public InstallStateStore Store { get; }
        public InstalledAppContext Context { get; }

        public Fixture()
        {
            Root = Path.Combine(Data, "install");
            var directory = InstallLayout.VersionDirectory(Root, "2.2.0");
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, InstallLayout.AppFileName);
            File.WriteAllText(executable, "never executed");
            Store = new InstallStateStore(Root);
            Store.Write(new InstallState { Current = "2.2.0", CurrentHealthy = true });
            Context = new InstalledAppContext(executable);
        }

        public string MakeSource(int version)
        {
            var directory = Path.Combine(Paths.BackupDirectory, "update", "AniMeido-rollback-from-2.2.0-20261019-120000");
            Directory.CreateDirectory(directory);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "AniMeido.db"),
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {version}";
            command.ExecuteNonQuery();
            return directory;
        }

        public void Dispose() => Paths.Dispose();
    }
}
