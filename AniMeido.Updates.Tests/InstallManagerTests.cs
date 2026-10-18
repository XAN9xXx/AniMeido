using AniMeido.Updates.Install;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class InstallManagerTests
{
    [Fact]
    public void PendingSwitchResetsHealthAndMigrationAndCountsFirstStart()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.3.0");
        temp.Save(healthy: false, failed: 2, previous: "2.1.0", pending: "2.3.0",
            migration: new MigrationRecord { Version = "2.2.0", Backup = temp.PathFor("before.db") });
        var decision = temp.Manager.PrepareLaunch();
        Assert.False(decision.NeedsRollbackChoice);
        var state = temp.Store.Read();
        Assert.Equal("2.3.0", state.Current);
        Assert.Equal("2.2.0", state.Previous);
        Assert.Null(state.Pending);
        Assert.False(state.CurrentHealthy);
        Assert.Equal(1, state.FailedStarts);
        Assert.Null(state.Migration);
    }

    [Fact]
    public void InvalidPendingIsRemovedWithoutAffectingHealthyCurrent()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.3.0", withExecutable: false);
        temp.Save(pending: "2.3.0");
        var decision = temp.Manager.PrepareLaunch();
        Assert.Equal("2.2.0", decision.State.Current);
        Assert.Null(decision.State.Pending);
        Assert.Equal(0, decision.State.FailedStarts);
        Assert.Contains(temp.Logs, message => message.Contains("pending"));
    }

    [Fact]
    public void TwoUnhealthyStartsThenThirdLaunchRollsBackAndNoticeIsConsumedOnce()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.1.9");
        temp.Save(healthy: false, previous: "2.1.9");
        Assert.Equal(1, temp.Manager.PrepareLaunch().State.FailedStarts);
        Assert.Equal(2, temp.Manager.PrepareLaunch().State.FailedStarts);
        var state = temp.Manager.PrepareLaunch().State;
        Assert.Equal("2.1.9", state.Current);
        Assert.Null(state.Previous);
        Assert.True(state.CurrentHealthy);
        Assert.Equal(0, state.FailedStarts);
        Assert.Equal(new[] { "2.2.0" }, state.FailedVersions);
        Assert.Equal(new RolledBackNotice { Version = "2.2.0", Reason = RolledBackNotice.StartFailed },
            temp.Manager.ConsumeRolledBackNotice());
        Assert.Null(temp.Manager.ConsumeRolledBackNotice());
        Assert.Null(temp.Store.Read().RolledBackFrom);
    }

    [Fact]
    public void MigrationForDifferentVersionDoesNotBlockAutomaticRollback()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.1.9");
        temp.Save(healthy: false, failed: 2, previous: "2.1.9",
            migration: new MigrationRecord { Version = "2.1.8", Backup = temp.PathFor("before.db") });
        var decision = temp.Manager.PrepareLaunch();
        Assert.False(decision.NeedsRollbackChoice);
        Assert.Equal("2.1.9", decision.State.Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2.1.9")]
    public void NoValidPreviousContinuesCountingButDoesNotRollback(string? previous)
    {
        using var temp = new InstallTestRoot();
        temp.Save(healthy: false, failed: 2, previous: previous);
        var decision = temp.Manager.PrepareLaunch();
        Assert.Equal("2.2.0", decision.State.Current);
        Assert.Equal(3, decision.State.FailedStarts);
        Assert.False(decision.State.CurrentHealthy);
        Assert.False(decision.NeedsRollbackChoice);
        Assert.Contains(temp.Logs, message => message.Contains("没有有效"));
    }

    [Fact]
    public void HealthyVersionNeverCountsFailures()
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0, temp.Manager.PrepareLaunch().State.FailedStarts);
        }
    }

    [Fact]
    public void MigrationRequiresChoiceAndKeepGivesTwoMoreOpportunities()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.1.9");
        temp.Save(healthy: false, failed: 2, previous: "2.1.9",
            migration: new MigrationRecord { Version = "2.2.0", Backup = temp.PathFor("before.db") });
        var request = temp.Manager.PrepareLaunch();
        Assert.True(request.NeedsRollbackChoice);
        Assert.Equal(2, request.State.FailedStarts);
        Assert.Equal("2.2.0", request.State.Current);
        var keep = temp.Manager.PrepareLaunch(choice =>
        {
            Assert.Equal("2.2.0", choice.Version);
            Assert.Equal("2.1.9", choice.Previous);
            return RollbackChoice.Keep;
        }, temp.PathFor("data"));
        Assert.False(keep.NeedsRollbackChoice);
        Assert.Equal(1, keep.State.FailedStarts);
        Assert.NotNull(keep.State.Migration);
        Assert.Empty(keep.State.FailedVersions);
        Assert.Equal(2, temp.Manager.PrepareLaunch().State.FailedStarts);
        Assert.True(temp.Manager.PrepareLaunch().NeedsRollbackChoice);
        Assert.False(Directory.Exists(temp.PathFor("data")));
    }

    [Fact]
    public void RestoreChoiceArchivesThenRestoresDataBeforeVersionSwitch()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.1.9");
        var backup = temp.MakeDatabase("before.db", "old");
        var database = temp.MakeDatabase("data/AniMeido.db", "new");
        temp.Save(healthy: false, failed: 2, previous: "2.1.9",
            migration: new MigrationRecord { Version = "2.2.0", Backup = backup });
        var state = temp.Manager.PrepareLaunch(_ => RollbackChoice.Restore, temp.PathFor("data"), () => true).State;
        Assert.Equal("2.1.9", state.Current);
        Assert.Equal(File.ReadAllBytes(backup), File.ReadAllBytes(database));
        Assert.Equal(RolledBackNotice.UserRestored, state.RolledBackFrom!.Reason);
        Assert.Null(state.Migration);
        Assert.Equal(new[] { "2.2.0" }, state.FailedVersions);
        Assert.Single(Directory.GetDirectories(temp.PathFor("data/Backups/update")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsafeOrFailedRestoreDoesNotSwitchOrResetFailureCount(bool noAppRunning)
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.1.9");
        var database = temp.MakeDatabase("data/AniMeido.db", "new");
        temp.Save(healthy: false, failed: 2, previous: "2.1.9",
            migration: new MigrationRecord { Version = "2.2.0", Backup = temp.PathFor("missing.db") });
        var bytes = File.ReadAllBytes(database);
        var stateBytes = File.ReadAllBytes(temp.Store.StatePath);
        Assert.Throws<InstallException>(() => temp.Manager.PrepareLaunch(_ => RollbackChoice.Restore,
            temp.PathFor("data"), () => noAppRunning));
        Assert.Equal(stateBytes, File.ReadAllBytes(temp.Store.StatePath));
        Assert.Equal(bytes, File.ReadAllBytes(database));
    }

    [Fact]
    public void MarkHealthyOnlyChangesMatchingVersion()
    {
        using var temp = new InstallTestRoot();
        temp.Save(healthy: false, failed: 2,
            migration: new MigrationRecord { Version = "2.2.0", Backup = temp.PathFor("before.db") });
        temp.Manager.MarkHealthy("2.1.9");
        Assert.False(temp.Store.Read().CurrentHealthy);
        Assert.Equal(2, temp.Store.Read().FailedStarts);
        temp.Manager.MarkHealthy("2.2.0");
        var healthy = temp.Store.Read();
        Assert.True(healthy.CurrentHealthy);
        Assert.Equal(0, healthy.FailedStarts);
        Assert.Null(healthy.Migration);
    }

    [Fact]
    public void RecordMigrationOnlyChangesCurrentVersion()
    {
        using var temp = new InstallTestRoot();
        temp.Save(healthy: false);
        temp.Manager.RecordMigration("2.1.9", temp.PathFor("before.db"));
        Assert.Null(temp.Store.Read().Migration);
        temp.Manager.RecordMigration("2.2.0", temp.PathFor("before.db"));
        Assert.Equal(new MigrationRecord { Version = "2.2.0", Backup = temp.PathFor("before.db") }, temp.Store.Read().Migration);
    }

    [Theory]
    [InlineData("2.2.0", true)]
    [InlineData("2.2.0+metadata", true)]
    [InlineData("2.1.9", true)]
    [InlineData("3.0.0", false)]
    [InlineData("3.0.0-preview.1.staging", true)]
    [InlineData("3.0.0+build.staging", true)]
    [InlineData("bad", true)]
    public void SetPendingRejectsInvalidMissingOrNotNewerVersion(string version, bool exists)
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        if (exists)
        {
            temp.AddVersion(version);
        }
        Assert.ThrowsAny<Exception>(() => temp.Manager.SetPending(version));
        Assert.Null(temp.Store.Read().Pending);
    }

    [Fact]
    public void SetPendingPreservesOtherFieldsAndAcceptsHigherPreview()
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        temp.AddVersion("2.3.0-preview.1");
        temp.Manager.SetPending("2.3.0-preview.1");
        var state = temp.Store.Read();
        Assert.Equal("2.2.0", state.Current);
        Assert.True(state.CurrentHealthy);
        Assert.Equal("2.3.0-preview.1", state.Pending);
    }

    [Fact]
    public async Task ConcurrentLaunchersRecheckProcessInsideLockAndDoNotDoubleCount()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.3.0");
        temp.Save(pending: "2.3.0");
        var running = 0;
        var starts = 0;
        var second = new InstallManager(new InstallStateStore(temp.Root));
        void Start(string executable)
        {
            Assert.Equal(InstallLayout.ExecutablePath(temp.Root, "2.3.0"), executable);
            Thread.Sleep(40); // 模拟创建进程的间隙；锁必须一直保持到可枚举进程。
            Volatile.Write(ref running, 1);
            Interlocked.Increment(ref starts);
        }
        await Task.WhenAll(
            Task.Run(() => temp.Manager.Launch(() => Volatile.Read(ref running) != 0, Start, _ => RollbackChoice.Keep, temp.PathFor("data"))),
            Task.Run(() => second.Launch(() => Volatile.Read(ref running) != 0, Start, _ => RollbackChoice.Keep, temp.PathFor("data"))));
        Assert.Equal(2, starts); // 允许多开，但只计第一次启动。
        Assert.Equal(1, temp.Store.Read().FailedStarts);
    }

    [Fact]
    public void RunningAppLaunchDoesNotSwitchCountOrCleanTemporaryState()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.3.0");
        temp.Save(healthy: false, failed: 2, pending: "2.3.0");
        var before = File.ReadAllBytes(temp.Store.StatePath);
        File.WriteAllText(temp.Store.StatePath + ".tmp", "untouched");
        var started = false;
        temp.Manager.Launch(() => true, executable =>
        {
            Assert.Equal(InstallLayout.ExecutablePath(temp.Root, "2.2.0"), executable);
            started = true;
        }, _ => throw new InvalidOperationException("must not ask"), temp.PathFor("data"));
        Assert.True(started);
        Assert.Equal(before, File.ReadAllBytes(temp.Store.StatePath));
        Assert.Equal("untouched", File.ReadAllText(temp.Store.StatePath + ".tmp"));
    }

    [Fact]
    public void RunningAppReadDoesNotWriteStateOrRemoveTemporaryFile()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.3.0");
        temp.Save(healthy: false, failed: 2, pending: "2.3.0");
        var before = File.ReadAllBytes(temp.Store.StatePath);
        File.WriteAllText(temp.Store.StatePath + ".tmp", "untouched");
        Assert.Equal(InstallLayout.ExecutablePath(temp.Root, "2.2.0"), temp.Manager.GetCurrentExecutableReadOnly());
        Assert.Equal(before, File.ReadAllBytes(temp.Store.StatePath));
        Assert.Equal("untouched", File.ReadAllText(temp.Store.StatePath + ".tmp"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PowerLossDuringEachLaunchTransitionKeepsExecutableValid(int stage)
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.3.0");
        temp.Save(pending: "2.3.0");
        var interrupted = new InstallManager(new InstallStateStore(temp.Root, null, null, checkpoint =>
        {
            if ((int)checkpoint == stage)
            {
                throw new IOException("power loss");
            }
        }));
        Assert.Throws<IOException>(() => interrupted.PrepareLaunch());
        var state = temp.Store.Read();
        Assert.True(InstallLayout.IsValidVersion(temp.Root, state.Current));
        Assert.Equal(stage == 3 ? "2.3.0" : "2.2.0", state.Current);
        Assert.Equal(stage == 3 ? 1 : 0, state.FailedStarts);
    }

    [Fact]
    public void StartCreationFailureStillCountsAttemptAndKeepsVersionUsable()
    {
        using var temp = new InstallTestRoot();
        temp.Save(healthy: false);
        Assert.Throws<IOException>(() => temp.Manager.Launch(() => false,
            _ => throw new IOException("cannot create process"), _ => RollbackChoice.Keep, temp.PathFor("data")));
        var state = temp.Store.Read();
        Assert.Equal(1, state.FailedStarts);
        Assert.Equal("2.2.0", state.Current);
        Assert.True(InstallLayout.IsValidVersion(temp.Root, state.Current));
    }

    public static IEnumerable<object[]> OperationFaultCases()
    {
        foreach (var operation in new[] { "pending", "healthy", "migration", "notice", "count", "rollback", "keep" })
        {
            for (var stage = 0; stage < 4; stage++)
            {
                yield return new object[] { operation, stage };
            }
        }
    }

    [Theory]
    [MemberData(nameof(OperationFaultCases))]
    public void EveryStateOperationIsAtomicAcrossEveryWriteBoundary(string operation, int failureStage)
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.1.9");
        temp.AddVersion("2.3.0");
        var before = temp.Save(healthy: false, failed: operation == "count" ? 0 : 2, previous: "2.1.9",
            migration: operation == "keep"
                ? new MigrationRecord { Version = "2.2.0", Backup = temp.PathFor("before.db") } : null)
            with { RolledBackFrom = new RolledBackNotice { Version = "2.4.0", Reason = RolledBackNotice.StartFailed } };
        temp.Store.Write(before);
        var interrupted = new InstallManager(new InstallStateStore(temp.Root, null, null, stage =>
        {
            if ((int)stage == failureStage)
            {
                throw new IOException("power loss");
            }
        }));
        Action execute;
        InstallState after;
        switch (operation)
        {
            case "pending":
                execute = () => interrupted.SetPending("2.3.0");
                after = before with { Pending = "2.3.0" };
                break;
            case "healthy":
                execute = () => interrupted.MarkHealthy("2.2.0");
                after = before with { CurrentHealthy = true, FailedStarts = 0, Migration = null };
                break;
            case "migration":
                execute = () => interrupted.RecordMigration("2.2.0", temp.PathFor("before.db"));
                after = before with { Migration = new MigrationRecord { Version = "2.2.0", Backup = temp.PathFor("before.db") } };
                break;
            case "notice":
                execute = () => interrupted.ConsumeRolledBackNotice();
                after = before with { RolledBackFrom = null };
                break;
            case "keep":
                execute = () => interrupted.PrepareLaunch(_ => RollbackChoice.Keep, temp.PathFor("data"));
                after = LaunchDecisions.Evaluate(before, _ => true, RollbackChoice.Keep).State;
                break;
            default:
                execute = () => interrupted.PrepareLaunch();
                after = LaunchDecisions.Evaluate(before, _ => true).State;
                break;
        }
        Assert.Throws<IOException>(execute);
        var read = temp.Store.Read();
        var expected = failureStage == 3 ? after : before;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected, InstallJsonContext.Default.InstallState),
            System.Text.Json.JsonSerializer.Serialize(read, InstallJsonContext.Default.InstallState));
        Assert.True(InstallLayout.IsValidVersion(temp.Root, read.Current));
    }

    [Fact]
    public void CleanupProtectsCurrentPreviousAndPendingAndCollectsIndividualFailures()
    {
        using var temp = new InstallTestRoot();
        var previous = temp.AddVersion("2.1.9");
        var pending = temp.AddVersion("2.3.0");
        var obsolete = temp.AddVersion("2.1.8");
        var locked = temp.AddVersion("2.1.7");
        var staging = temp.AddVersion("2.4.0.staging");
        var other = temp.PathFor("unrelated");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(temp.PathFor("other.staging"));
        var packages = temp.PathFor("packages");
        Directory.CreateDirectory(packages);
        var keepPackage = Path.Combine(packages, "AniMeido-2.3.0-win-x64.zip");
        File.WriteAllText(keepPackage, "keep");
        File.WriteAllText(keepPackage + ".partial", "keep");
        var deletePackage = Path.Combine(packages, "old.zip");
        var lockedPackage = Path.Combine(packages, "locked.zip");
        File.WriteAllText(deletePackage, "delete");
        File.WriteAllText(lockedPackage, "locked");
        temp.Save(previous: "2.1.9", pending: "2.3.0");
        using var heldExe = new FileStream(Path.Combine(locked, InstallLayout.AppFileName), FileMode.Open, FileAccess.Read, FileShare.None);
        using var heldPackage = new FileStream(lockedPackage, FileMode.Open, FileAccess.Read, FileShare.None);
        var errors = temp.Manager.CleanupObsolete();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error => error.Path == locked);
        Assert.Contains(errors, error => error.Path == lockedPackage);
        Assert.True(Directory.Exists(InstallLayout.VersionDirectory(temp.Root, "2.2.0")));
        Assert.True(Directory.Exists(previous));
        Assert.True(Directory.Exists(pending));
        Assert.True(Directory.Exists(other));
        Assert.False(Directory.Exists(obsolete));
        Assert.False(Directory.Exists(staging));
        Assert.False(Directory.Exists(temp.PathFor("other.staging")));
        Assert.False(File.Exists(keepPackage));
        Assert.True(File.Exists(keepPackage + ".partial"));
        Assert.False(File.Exists(deletePackage));
    }

    [Fact]
    public void CleanupWithoutPendingDeletesCompletedPackagesButLeavesOtherRootFiles()
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        Directory.CreateDirectory(temp.PathFor("packages"));
        File.WriteAllText(temp.PathFor("packages/package.zip"), "obsolete");
        File.WriteAllText(temp.PathFor("AniMeido.exe"), "launcher");
        Assert.Empty(temp.Manager.CleanupObsolete());
        Assert.Empty(Directory.GetFiles(temp.PathFor("packages")));
        Assert.True(File.Exists(temp.PathFor("AniMeido.exe")));
        Assert.True(File.Exists(temp.Store.StatePath));
    }

    [Fact]
    public void UpdateLockProtectsPackagesStagingAndUnregisteredVersionFromOtherManager()
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        var second = new InstallManager(new InstallStateStore(temp.Root));
        var target = temp.AddVersion("2.3.0");
        var staging = temp.AddVersion("2.4.0.staging");
        Directory.CreateDirectory(temp.PathFor("packages"));
        var zip = temp.PathFor("packages/prepared.zip");
        File.WriteAllText(zip, "closed ZIP awaiting extraction");

        using (temp.Manager.AcquireUpdateLock())
        {
            Assert.Throws<InstallException>(() => second.AcquireUpdateLock());
            Assert.Single(second.CleanupObsolete());
            Assert.True(File.Exists(zip));
            Assert.True(Directory.Exists(staging));
            Assert.True(Directory.Exists(target));
            Assert.Null(second.ReadState().Pending); // 更新锁不能长时间占用 state.lock。
            temp.Manager.SetPending("2.3.0");
        }

        Assert.Empty(second.CleanupObsolete());
        Assert.True(Directory.Exists(target));
        Assert.Equal("2.3.0", second.ReadState().Pending);
        Assert.False(File.Exists(zip));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void CleanupPackagesDeletesAllZipAndOnlyExpiredPartial()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.3.0");
        temp.Save(pending: "2.3.0");
        var packages = temp.PathFor("packages");
        Directory.CreateDirectory(packages);
        var completed = Path.Combine(packages, new string('a', 64) + ".zip");
        var fresh = completed + ".partial";
        var expired = Path.Combine(packages, "expired.zip.partial");
        var unrelated = Path.Combine(packages, "notes.txt");
        foreach (var path in new[] { completed, fresh, expired, unrelated })
            File.WriteAllText(path, "test-only");
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddDays(-13));
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-15));
        Assert.Empty(temp.Manager.CleanupObsolete());
        Assert.False(File.Exists(completed));
        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(unrelated));
    }
}
