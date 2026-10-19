using System.Text.Json;
using AniMeido.Updates.Install;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class InstallStateStoreTests
{
    [Fact]
    public void RoundtripContainsEverySchemaField()
    {
        using var temp = new InstallTestRoot();
        var state = new InstallState
        {
            Current = "2.2.0",
            Previous = "2.1.9",
            Pending = "2.3.0-preview.1+build.2",
            CurrentHealthy = false,
            FailedStarts = 2,
            Migration = new MigrationRecord { Version = "2.2.0", Backup = temp.PathFor("before.db") },
            RolledBackFrom = new RolledBackNotice { Version = "2.4.0", Reason = RolledBackNotice.UserRestored },
            FailedVersions = ["2.4.0", "2.3.9"],
        };
        temp.Store.Write(state);
        var actual = temp.Store.Read();
        Assert.Equal(state.Current, actual.Current);
        Assert.Equal(state.Previous, actual.Previous);
        Assert.Equal(state.Pending, actual.Pending);
        Assert.Equal(state.CurrentHealthy, actual.CurrentHealthy);
        Assert.Equal(state.FailedStarts, actual.FailedStarts);
        Assert.Equal(state.Migration, actual.Migration);
        Assert.Equal(state.RolledBackFrom, actual.RolledBackFrom);
        Assert.Equal(state.FailedVersions, actual.FailedVersions);
        using var document = JsonDocument.Parse(File.ReadAllBytes(temp.Store.StatePath));
        Assert.Equal(new[] { "schema", "current", "previous", "pending", "currentHealthy", "failedStarts",
            "migration", "rolledBackFrom", "pendingRestore", "lastRestore", "failedVersions" },
            document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.False(File.Exists(temp.Store.StatePath + ".tmp"));
    }

    [Theory]
    [InlineData("partial json")]
    [InlineData("{\"schema\":1,\"current\":\"9.0.0\"}")]
    public void ValidFormalFileWinsOverLeftoverTemporaryFile(string leftover)
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        File.WriteAllText(temp.Store.StatePath + ".tmp", leftover);
        Assert.Equal("2.2.0", temp.Store.Read().Current);
        Assert.False(File.Exists(temp.Store.StatePath + ".tmp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("broken json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schema\":2,\"current\":\"2.2.0\",\"currentHealthy\":false,\"failedStarts\":0,\"failedVersions\":[]}")]
    [InlineData("{\"schema\":1,\"current\":\"not-semver\",\"currentHealthy\":false,\"failedStarts\":0,\"failedVersions\":[]}")]
    [InlineData("{\"schema\":1,\"current\":\"2.2.0\",\"currentHealthy\":false,\"failedStarts\":-1,\"failedVersions\":[]}")]
    [InlineData("{\"schema\":1,\"current\":\"2.2.0\",\"currentHealthy\":false,\"failedStarts\":0,\"failedVersions\":null}")]
    [InlineData("{\"schema\":1,\"current\":\"9.0.0\",\"currentHealthy\":false,\"failedStarts\":0,\"failedVersions\":[]}")]
    public void RecoveryScansCompleteVersionsAndSelectsHighestSemver(string? damaged)
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.9.0");
        temp.AddVersion("2.10.0-preview.1");
        temp.AddVersion("2.10.0");
        temp.AddVersion("99.0.0", withExecutable: false);
        temp.AddVersion("100.0.0.staging");
        temp.AddVersion("invalid");
        if (damaged is not null)
        {
            File.WriteAllText(temp.Store.StatePath, damaged);
        }
        File.WriteAllText(temp.Store.StatePath + ".tmp", "uncommitted");
        var recovered = temp.Store.Read();
        Assert.Equal("2.10.0", recovered.Current);
        Assert.True(recovered.CurrentHealthy);
        Assert.Equal(0, recovered.FailedStarts);
        Assert.Null(recovered.Previous);
        Assert.Null(recovered.Pending);
        Assert.Null(recovered.Migration);
        Assert.Null(recovered.RolledBackFrom);
        Assert.Empty(recovered.FailedVersions);
        Assert.Contains(temp.Logs, message => message.Contains("原因"));
        Assert.Equal("2.10.0", temp.Store.Read().Current);
        Assert.False(File.Exists(temp.Store.StatePath + ".tmp"));
    }

    [Fact]
    public void NoUsableVersionIsExplicitFailure()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.PathFor("app-2.2.0"));
        var error = Assert.Throws<InstallException>(() => new InstallStateStore(temp.Root).Read());
        Assert.Contains("没有", error.Message);
        Assert.Contains("AniMeido.App.exe", error.Message);
        Assert.False(File.Exists(temp.PathFor("state.json")));
    }

    [Fact]
    public void HeldLockTimesOutWithoutChangingState()
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        var before = File.ReadAllBytes(temp.Store.StatePath);
        using var held = new FileStream(temp.PathFor("state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var blocked = new InstallStateStore(temp.Root, TimeSpan.FromMilliseconds(80));
        Assert.Contains("state.lock", Assert.Throws<InstallException>(() => blocked.Read()).Message);
        Assert.Throws<InstallException>(() => blocked.Write(new InstallState { Current = "2.2.0" }));
        Assert.Equal(before, File.ReadAllBytes(temp.Store.StatePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PowerLossAtEveryWriteBoundaryPreservesOldOrNewState(int failureStage)
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.3.0");
        var old = temp.Save();
        var next = old with { Current = "2.3.0", Previous = "2.2.0", CurrentHealthy = false, FailedStarts = 1 };
        var interrupted = new InstallStateStore(temp.Root, null, null, stage =>
        {
            if ((int)stage == failureStage)
            {
                throw new IOException("simulated power loss");
            }
        });
        Assert.Throws<IOException>(() => interrupted.Write(next));
        var recovered = temp.Store.Read();
        Assert.Contains(recovered.Current, new[] { old.Current, next.Current });
        Assert.True(InstallLayout.IsValidVersion(temp.Root, recovered.Current));
        Assert.Equal(failureStage == 3 ? next.Current : old.Current, recovered.Current);
        Assert.Equal(failureStage == 3 ? next.Previous : old.Previous, recovered.Previous);
        Assert.Equal(failureStage == 3 ? next.FailedStarts : old.FailedStarts, recovered.FailedStarts);
        Assert.Equal(failureStage == 3 ? next.CurrentHealthy : old.CurrentHealthy, recovered.CurrentHealthy);
    }

    [Fact]
    public void MissingFormalFileDoesNotPromoteIncompleteTemporaryState()
    {
        using var temp = new InstallTestRoot();
        File.WriteAllText(temp.Store.StatePath + ".tmp", "{\"schema\":1,");
        var recovered = temp.Store.Read();
        Assert.Equal("2.2.0", recovered.Current);
        Assert.True(recovered.CurrentHealthy);
    }

    [Fact]
    public void WriterRejectsMissingExecutableAndInvalidFields()
    {
        using var temp = new InstallTestRoot();
        var state = temp.Save();
        Assert.Throws<InstallException>(() => temp.Store.Write(state with { Current = "3.0.0" }));
        Assert.Throws<InstallException>(() => temp.Store.Write(state with { Previous = "../bad" }));
        Assert.Throws<InstallException>(() => temp.Store.Write(state with { Pending = "2.03.0" }));
        Assert.Throws<InstallException>(() => temp.Store.Write(state with { FailedVersions = ["2"] }));
        Assert.Throws<InstallException>(() => temp.Store.Write(state with
        {
            Migration = new MigrationRecord { Version = "bad", Backup = temp.PathFor("backup.db") },
        }));
        Assert.Throws<InstallException>(() => temp.Store.Write(state with
        {
            RolledBackFrom = new RolledBackNotice { Version = "2.3.0", Reason = "unknown" },
        }));
        Assert.Equal("2.2.0", temp.Store.Read().Current);
    }
}
