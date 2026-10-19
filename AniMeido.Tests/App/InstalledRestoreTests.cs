using System.Buffers.Binary;
using System.Text;
using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;
using AniMeido.Updates.Install;

namespace AniMeido.Tests;

public sealed class InstalledRestoreTests
{
    [Fact]
    public void AppChecksActualSchemaBeforeWritingRequestAndDoesNotReplaceDatabase()
    {
        using var fixture = new Fixture();
        var source = fixture.MakeSource(DatabaseSchema.CurrentVersion + 1);
        File.WriteAllText(fixture.Paths.DatabasePath, "current synthetic database");
        Assert.Contains("请先升级", Assert.Throws<InstallException>(() => fixture.Context.ValidateRestore(fixture.Data, source)).Message);
        Assert.Throws<InstallException>(() => fixture.Context.RequestRestore(fixture.Data, source));
        Assert.Null(fixture.Store.Read().PendingRestore);
        fixture.MakeSource(DatabaseSchema.CurrentVersion);
        Assert.Equal(source, fixture.Context.ValidateRestore(fixture.Data, source));
        fixture.Context.RequestRestore(fixture.Data, source);
        Assert.Equal(source, fixture.Store.Read().PendingRestore!.Directory);
        Assert.Equal("current synthetic database", File.ReadAllText(fixture.Paths.DatabasePath));
    }

    [Fact]
    public void ConfirmationDelayCannotBypassRevalidationOrUpdateMutualExclusion()
    {
        using var fixture = new Fixture();
        var source = fixture.MakeSource(DatabaseSchema.CurrentVersion);
        fixture.Context.ValidateRestore(fixture.Data, source);
        fixture.MakeSource(DatabaseSchema.CurrentVersion + 1);
        Assert.Throws<InstallException>(() => fixture.Context.RequestRestore(fixture.Data, source));
        fixture.MakeSource(DatabaseSchema.CurrentVersion);
        var next = InstallLayout.VersionDirectory(fixture.Root, "2.3.0");
        Directory.CreateDirectory(next);
        File.WriteAllText(Path.Combine(next, InstallLayout.AppFileName), "never executed");
        fixture.Store.Write(fixture.Store.Read() with { Pending = "2.3.0" });
        Assert.Throws<InstallException>(() => fixture.Context.ValidateRestore(fixture.Data, source));
        Assert.Throws<InstallException>(() => fixture.Context.RequestRestore(fixture.Data, source));
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
    public void PortableModeCannotRequestRestoreAndHasNoRestoreNotice()
    {
        using var paths = new MockAppDataPaths();
        var context = new InstalledAppContext(Path.Combine(paths.RootDirectory, "debug", InstallLayout.AppFileName));
        Assert.Throws<InvalidOperationException>(() => context.ValidateRestore(paths.RootDirectory, paths.RootDirectory));
        Assert.Throws<InvalidOperationException>(() => context.RequestRestore(paths.RootDirectory, paths.RootDirectory));
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
            var bytes = new byte[100];
            Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(60, 4), version);
            File.WriteAllBytes(Path.Combine(directory, "AniMeido.db"), bytes);
            return directory;
        }

        public void Dispose() => Paths.Dispose();
    }
}
