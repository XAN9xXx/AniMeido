using AniMeido.App.Services;
using AniMeido.Updates.Install;

namespace AniMeido.Tests;

public sealed class InstalledAppContextTests : DbTestBase
{
    [Theory]
    [InlineData("app-2.2.0", true, true)]
    [InlineData("app-2.3.0-preview.1+build.2", true, true)]
    [InlineData("app-2.2.0", false, false)]
    [InlineData("app-2.2.0.staging", true, false)]
    [InlineData("app-2.3.0-preview.1.staging", true, false)]
    [InlineData("app-2.02.0", true, false)]
    [InlineData("app-bad", true, false)]
    [InlineData("debug", true, false)]
    [InlineData("AniMeido", true, false)]
    public void DetectsOnlyValidVersionDirectoryWithParentState(string directoryName, bool hasState, bool expected)
    {
        var root = Path.Combine(Paths.RootDirectory, "install");
        var directory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(directory);
        if (hasState)
        {
            File.WriteAllText(Path.Combine(root, "state.json"), "existence-only mode detection");
        }
        var context = new InstalledAppContext(Path.Combine(directory, InstallLayout.AppFileName));
        Assert.Equal(expected, context.IsInstalled);
        Assert.Equal(expected ? root : null, context.InstallRoot);
        Assert.Equal(expected ? directoryName[4..] : null, context.Version);
    }

    [Fact]
    public void NonInstalledOperationsDoNothingAndDoNotCreateAnyFiles()
    {
        var root = Paths.RootDirectory;
        var context = new InstalledAppContext(Path.Combine(root, "debug", InstallLayout.AppFileName));
        var before = Directory.GetFileSystemEntries(root);
        context.MarkHealthy();
        context.RecordMigration(Path.Combine(root, "does-not-exist.db"));
        Assert.Null(context.ConsumeRolledBackNotice());
        Assert.Empty(context.CleanupObsolete());
        Assert.Equal(before, Directory.GetFileSystemEntries(root));
        Assert.False(new InstalledAppContext(null).IsInstalled);
    }

    [Fact]
    public void InstalledOperationsUseDirectoryVersionAndConsumeNoticeOnce()
    {
        var root = Path.Combine(Paths.RootDirectory, "install");
        var directory = InstallLayout.VersionDirectory(root, "2.2.0");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, InstallLayout.AppFileName);
        File.WriteAllText(executable, "test-only");
        var store = new InstallStateStore(root);
        store.Write(new InstallState
        {
            Current = "2.2.0",
            FailedStarts = 2,
            RolledBackFrom = new RolledBackNotice { Version = "2.3.0", Reason = RolledBackNotice.StartFailed },
        });
        var context = new InstalledAppContext(executable);
        context.RecordMigration(Path.Combine(Paths.RootDirectory, "before.db"));
        Assert.Equal("2.2.0", store.Read().Migration!.Version);
        context.MarkHealthy();
        var state = store.Read();
        Assert.True(state.CurrentHealthy);
        Assert.Equal(0, state.FailedStarts);
        Assert.Null(state.Migration);
        Assert.Equal("2.3.0", context.ConsumeRolledBackNotice()!.Version);
        Assert.Null(context.ConsumeRolledBackNotice());
    }
}
