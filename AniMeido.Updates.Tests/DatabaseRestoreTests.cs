using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using AniMeido.Updates.Install;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class DatabaseRestoreTests
{
    [Fact]
    public void OldSchemaOneWithoutOptionalFieldsStillReadsNormally()
    {
        using var temp = new InstallTestRoot();
        File.WriteAllText(temp.Store.StatePath,
            """{"schema":1,"current":"2.2.0","currentHealthy":false,"failedStarts":1,"failedVersions":[],"previous":"2.1.0"}""");
        var state = temp.Store.Read();
        Assert.Equal(1, state.Schema);
        Assert.False(state.CurrentHealthy);
        Assert.Equal(1, state.FailedStarts);
        Assert.Equal("2.1.0", state.Previous);
        Assert.Null(state.PendingRestore);
        Assert.Null(state.LastRestore);
        Assert.Empty(temp.Logs);
    }

    [Fact]
    public void RequestRoundtripsAndLaunchRestoresBeforeStartingAndClearsRequest()
    {
        using var temp = new InstallTestRoot();
        temp.Save(healthy: false, failed: 2, previous: "2.1.0");
        temp.AddVersion("2.1.0");
        var database = MakeDatabase(temp, "data/AniMeido.db", 7);
        File.WriteAllText(database + "-wal", "current wal");
        File.WriteAllText(database + "-shm", "current shm");
        var original = File.ReadAllBytes(database);
        var source = MakeSource(temp, 8);
        File.WriteAllText(Path.Combine(source, "AniMeido.db-wal"), "source wal");
        File.WriteAllText(Path.Combine(source, "AniMeido.db-shm"), "source shm");
        var beforeRequest = DateTimeOffset.UtcNow;
        temp.Manager.RequestRestore(temp.PathFor("data"), source, 8);
        var request = temp.Store.Read().PendingRestore!;
        Assert.Equal(source, request.Directory);
        Assert.InRange(request.RequestedAt, beforeRequest, DateTimeOffset.UtcNow);
        var reads = 0;
        var starts = 0;
        temp.Manager.Launch(() => false, executable =>
        {
            starts++;
            Assert.Equal(InstallLayout.ExecutablePath(temp.Root, "2.2.0"), executable);
            var state = temp.Store.ReadExistingLocked();
            Assert.Null(state.PendingRestore);
            Assert.True(state.LastRestore!.Success);
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, "AniMeido.db")), File.ReadAllBytes(database));
            Assert.Equal("source wal", File.ReadAllText(database + "-wal"));
            Assert.Equal("source shm", File.ReadAllText(database + "-shm"));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(state.LastRestore.Archive!, "AniMeido.db")));
            Assert.Equal("current wal", File.ReadAllText(Path.Combine(state.LastRestore.Archive!, "AniMeido.db-wal")));
            Assert.Equal("current shm", File.ReadAllText(Path.Combine(state.LastRestore.Archive!, "AniMeido.db-shm")));
            Assert.StartsWith("AniMeido-before-restore-", Path.GetFileName(state.LastRestore.Archive));
        }, _ => throw new InvalidOperationException("fresh restored data must not immediately roll back"), temp.PathFor("data"),
            executable => { reads++; return 8; });
        Assert.Equal(1, reads);
        Assert.Equal(1, starts);
        var restored = temp.Store.Read();
        Assert.Equal(1, restored.FailedStarts);
        Assert.Null(restored.Migration);
        Assert.Equal(source, restored.LastRestore!.Directory);
        temp.Manager.ClearRestoreNotice(restored.LastRestore);
        Assert.Null(temp.Manager.ReadRestoreNotice());
        Assert.Equal(1, temp.Store.Read().Schema);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(8)]
    public void VersionAtOrBelowCurrentIsAllowed(int version)
    {
        using var temp = new InstallTestRoot();
        var source = MakeSource(temp, version);
        Assert.Equal(source, DatabaseRollback.ValidateRestore(temp.PathFor("data"), source, 8));
    }

    [Fact]
    public void ReadsBigEndianHeaderWithoutOpeningSqliteOrCreatingSidecars()
    {
        using var temp = new InstallTestRoot();
        var file = MakeDatabase(temp, "header-only.db", 0x01020304);
        // 只有 100 字节文件头，不是可打开的完整 SQLite 数据库。
        var before = Snapshot(temp.Root);
        Assert.Equal(0x01020304, DatabaseRollback.ReadUserVersion(file));
        Assert.Equal(before, Snapshot(temp.Root));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name == "Microsoft.Data.Sqlite");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(16, true)]
    [InlineData(63, true)]
    [InlineData(100, false)]
    public void TruncatedOrNonSqliteFileIsRejectedWithoutWrites(int length, bool sqliteHeader)
    {
        using var temp = new InstallTestRoot();
        var file = MakeDatabase(temp, "bad.db", 8);
        var bytes = File.ReadAllBytes(file)[..length];
        if (!sqliteHeader)
            bytes[0] = 0;
        File.WriteAllBytes(file, bytes);
        var before = Snapshot(temp.Root);
        Assert.ThrowsAny<IOException>(() => DatabaseRollback.ReadUserVersion(file));
        Assert.Equal(before, Snapshot(temp.Root));
    }

    [Fact]
    public void NewerDataIsRejectedByAppRequestAndRecheckedAgainstChangedProgramAtLaunch()
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        MakeDatabase(temp, "data/AniMeido.db", 7);
        var source = MakeSource(temp, 9);
        var before = Snapshot(temp.Root);
        Assert.Contains("请先升级", Assert.Throws<InstallException>(() =>
            temp.Manager.ValidateRestore(temp.PathFor("data"), source, 8)).Message);
        Assert.Throws<InstallException>(() => temp.Manager.RequestRestore(temp.PathFor("data"), source, 8));
        // update.lock 是持久协调锁；除它之外不应改动数据/状态。
        Assert.Equal(before, Snapshot(temp.Root).Where(value => !value.Contains("update.lock:")).ToArray());
        temp.Manager.RequestRestore(temp.PathFor("data"), source, 9);
        var dataBefore = Snapshot(temp.PathFor("data"));
        temp.Manager.Launch(() => false, _ => { }, _ => RollbackChoice.Keep, temp.PathFor("data"), _ => 8);
        var notice = temp.Store.Read().LastRestore!;
        Assert.False(notice.Success);
        Assert.Contains("请先升级", notice.Error);
        Assert.Null(notice.Archive);
        Assert.Null(temp.Store.Read().PendingRestore);
        Assert.Equal(dataBefore, Snapshot(temp.PathFor("data")));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void MissingSourceSidecarRemovesCurrentCounterpart(string missingSuffix)
    {
        using var temp = new InstallTestRoot();
        var database = MakeDatabase(temp, "data/AniMeido.db", 7);
        File.WriteAllText(database + "-wal", "old wal");
        File.WriteAllText(database + "-shm", "old shm");
        var source = MakeSource(temp, 8, beforeRestore: true);
        var presentSuffix = missingSuffix == "-wal" ? "-shm" : "-wal";
        File.WriteAllText(Path.Combine(source, "AniMeido.db" + presentSuffix), "source");
        var archive = DatabaseRollback.GetRestoreArchiveDirectory(temp.PathFor("data"));
        var result = DatabaseRollback.RestoreArchived(temp.PathFor("data"), source, archive, 8);
        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(database + missingSuffix));
        Assert.Equal("source", File.ReadAllText(database + presentSuffix));
        Assert.True(File.Exists(Path.Combine(archive, "AniMeido.db" + missingSuffix)));
    }

    [Fact]
    public void LockedCurrentWalPreventsArchiveAndChangesNothing()
    {
        using var temp = new InstallTestRoot();
        var database = MakeDatabase(temp, "data/AniMeido.db", 7);
        var source = MakeSource(temp, 8);
        File.WriteAllText(database + "-wal", "current wal");
        var original = File.ReadAllBytes(database);
        using var held = new FileStream(database + "-wal", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = DatabaseRollback.RestoreArchived(temp.PathFor("data"), source,
            DatabaseRollback.GetRestoreArchiveDirectory(temp.PathFor("data")), 8);
        Assert.False(result.Success);
        Assert.Null(result.ArchiveDirectory);
        Assert.Equal(original, File.ReadAllBytes(database));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void MidReplacementFailurePutsBackDatabaseAndAllSidecars(string failSuffix)
    {
        using var temp = new InstallTestRoot();
        var database = MakeDatabase(temp, "data/AniMeido.db", 7);
        var source = MakeSource(temp, 8);
        File.WriteAllText(database + "-wal", "current wal");
        File.WriteAllText(database + "-shm", "current shm");
        File.WriteAllText(Path.Combine(source, "AniMeido.db-wal"), "source wal");
        File.WriteAllText(Path.Combine(source, "AniMeido.db-shm"), "source shm");
        var original = File.ReadAllBytes(database);
        var archive = DatabaseRollback.GetRestoreArchiveDirectory(temp.PathFor("data"));
        var result = DatabaseRollback.RestoreArchived(temp.PathFor("data"), source, archive, 8, suffix =>
        {
            if (suffix == failSuffix)
                throw new IOException("simulated sidecar installation failure");
        });
        Assert.False(result.Success);
        Assert.Equal(archive, result.ArchiveDirectory);
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Equal("current wal", File.ReadAllText(database + "-wal"));
        Assert.Equal("current shm", File.ReadAllText(database + "-shm"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(archive, "AniMeido.db")));
        Assert.Empty(Directory.GetFiles(archive, "*.detached"));
        Assert.Empty(Directory.GetFiles(temp.PathFor("data"), "*.restore-tmp*"));
    }

    [Fact]
    public void UnexpectedFileIsNeverOverwrittenAndOriginalIsKeptInArchive()
    {
        using var temp = new InstallTestRoot();
        var database = MakeDatabase(temp, "data/AniMeido.db", 7);
        var original = File.ReadAllBytes(database);
        var source = MakeSource(temp, 8);
        var archive = DatabaseRollback.GetRestoreArchiveDirectory(temp.PathFor("data"));
        var result = DatabaseRollback.RestoreArchived(temp.PathFor("data"), source, archive, 8,
            _ => File.WriteAllText(database, "unexpected external file"));
        Assert.False(result.Success);
        Assert.Contains("无法放回", result.Error);
        Assert.Equal("unexpected external file", File.ReadAllText(database));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(archive, "AniMeido.db.detached")));
    }

    [Fact]
    public void FailureIsRecordedAndAppStillStartsAfterOriginalFilesArePutBack()
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        var database = MakeDatabase(temp, "data/AniMeido.db", 7);
        File.WriteAllText(database + "-wal", "current wal");
        File.WriteAllText(database + "-shm", "current shm");
        var original = File.ReadAllBytes(database);
        var source = MakeSource(temp, 8);
        temp.Manager.RequestRestore(temp.PathFor("data"), source, 8);
        using var held = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read);
        var started = false;
        temp.Manager.Launch(() => false, _ => started = true, _ => RollbackChoice.Keep, temp.PathFor("data"), _ => 8);
        Assert.True(started);
        var notice = temp.Manager.ReadRestoreNotice()!;
        Assert.False(notice.Success);
        Assert.NotNull(notice.Error);
        Assert.NotNull(notice.Archive);
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Equal("current wal", File.ReadAllText(database + "-wal"));
        Assert.Equal("current shm", File.ReadAllText(database + "-shm"));
        Assert.Null(temp.Store.Read().PendingRestore);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunningAppRetainsRequestAndDoesNotTouchDataOrState(bool runningAtFirstCheck)
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        MakeDatabase(temp, "data/AniMeido.db", 7);
        var source = MakeSource(temp, 8);
        temp.Manager.RequestRestore(temp.PathFor("data"), source, 8);
        var before = Snapshot(temp.Root);
        var checks = 0;
        temp.Manager.Launch(() => runningAtFirstCheck || ++checks > 1, _ => { }, _ => RollbackChoice.Keep,
            temp.PathFor("data"), _ => throw new InvalidOperationException("must not read version while app runs"));
        Assert.Equal(before, Snapshot(temp.Root));
        Assert.NotNull(temp.Store.Read().PendingRestore);
    }

    [Fact]
    public void PendingVersionAndRestoreAreMutuallyExclusiveInBothDirections()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.3.0");
        temp.Save(pending: "2.3.0");
        var source = MakeSource(temp, 8);
        Assert.Throws<InstallException>(() => temp.Manager.RequestRestore(temp.PathFor("data"), source, 8));
        Assert.Equal("2.3.0", temp.Store.Read().Pending);
        temp.Save();
        temp.Manager.RequestRestore(temp.PathFor("data"), source, 8);
        Assert.Throws<InstallException>(() => temp.Manager.SetPending("2.3.0"));
        Assert.Throws<InstallException>(() => temp.Manager.RequestRestore(temp.PathFor("data"), source, 8));
        Assert.NotNull(temp.Store.Read().PendingRestore);
        Assert.Null(temp.Store.Read().Pending);
    }

    [Theory]
    [InlineData("data/Backups/update/before-update")]
    [InlineData("outside/AniMeido-rollback-from-2.2.0")]
    [InlineData("data/Backups/update/nested/AniMeido-before-restore-20261019-120000")]
    public void RejectsOtherPrefixesAndOutsideOrNestedDirectories(string relative)
    {
        using var temp = new InstallTestRoot();
        var path = temp.PathFor(relative);
        Directory.CreateDirectory(path);
        Assert.Throws<IOException>(() => RollbackDataDirectory.Validate(temp.PathFor("data/Backups/update"), path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsReparseDirectoryOrSourceFile(bool linkFile)
    {
        using var temp = new InstallTestRoot();
        var source = MakeSource(temp, 8);
        var outside = MakeDatabase(temp, "outside/AniMeido.db", 8);
        if (linkFile)
        {
            File.Delete(Path.Combine(source, "AniMeido.db"));
            File.CreateSymbolicLink(Path.Combine(source, "AniMeido.db"), outside);
            Assert.Throws<IOException>(() => DatabaseRollback.ValidateRestore(temp.PathFor("data"), source, 8));
        }
        else
        {
            var link = temp.PathFor("data/Backups/update/AniMeido-before-restore-link");
            Directory.CreateSymbolicLink(link, Path.GetDirectoryName(outside)!);
            Assert.Throws<IOException>(() => RollbackDataDirectory.Validate(temp.PathFor("data/Backups/update"), link));
            Directory.Delete(link);
        }
    }

    [Fact]
    public void OptionalStateFieldsValidateOnlyAbsolutePathFormatAndNoticeClearDoesNotLoseNewerResult()
    {
        using var temp = new InstallTestRoot();
        var state = temp.Save();
        var request = new PendingRestoreRequest { Directory = temp.PathFor("not-yet-present"), RequestedAt = DateTimeOffset.UtcNow };
        temp.Store.Write(state with { PendingRestore = request });
        Assert.Equal(request, temp.Store.Read().PendingRestore);
        Assert.Throws<InstallException>(() => temp.Store.Write(state with { PendingRestore = request with { Directory = "relative" } }));
        var notice = new RestoreNotice { Directory = request.Directory, Success = false, Error = "error", At = DateTimeOffset.UtcNow };
        temp.Store.Write(state with { LastRestore = notice });
        Assert.Throws<InstallException>(() => temp.Store.Write(state with { LastRestore = notice with { Archive = "relative" } }));
        var newer = notice with { At = notice.At.AddSeconds(1) };
        temp.Store.Write(state with { LastRestore = newer });
        temp.Manager.ClearRestoreNotice(notice);
        Assert.Equal(newer, temp.Manager.ReadRestoreNotice());
        temp.Manager.ClearRestoreNotice(newer);
        Assert.Null(temp.Manager.ReadRestoreNotice());
    }

    [Fact]
    public void UnprocessedPendingRestoreCanBeReadAndClearedWithoutLosingNewerRequest()
    {
        using var temp = new InstallTestRoot();
        var state = temp.Save();
        Assert.Null(temp.Manager.ReadPendingRestore());
        var request = new PendingRestoreRequest { Directory = temp.PathFor("old-request"), RequestedAt = DateTimeOffset.UtcNow };
        temp.Store.Write(state with { PendingRestore = request });
        Assert.Equal(request, temp.Manager.ReadPendingRestore());
        var newer = request with { RequestedAt = request.RequestedAt.AddSeconds(1) };
        temp.Store.Write(state with { PendingRestore = newer });
        temp.Manager.ClearPendingRestore(request);
        Assert.Equal(newer, temp.Manager.ReadPendingRestore());
        temp.Manager.ClearPendingRestore(newer);
        Assert.Null(temp.Manager.ReadPendingRestore());
    }

    [Fact]
    public void LauncherReadsTargetProgramsConstantMetadataWithoutLoadingAssembly()
    {
        using var temp = new InstallTestRoot();
        var executable = InstallLayout.ExecutablePath(temp.Root, "2.2.0");
        var assembly = Path.Combine(Path.GetDirectoryName(executable)!, "AniMeido.Plugin.Base.dll");
        WriteSchemaAssembly(assembly, 8);
        Assert.Equal(8, InstalledDatabaseVersion.Read(executable));
        WriteSchemaAssembly(assembly, 9);
        Assert.Equal(9, InstalledDatabaseVersion.Read(executable));
        File.WriteAllText(assembly, "invalid assembly");
        Assert.Throws<BadImageFormatException>(() => InstalledDatabaseVersion.Read(executable));
    }

    private static string MakeSource(InstallTestRoot temp, int version, bool beforeRestore = false)
    {
        var name = beforeRestore ? "AniMeido-before-restore-20261019-120000" : "AniMeido-rollback-from-2.2.0-20261019-120000";
        var database = MakeDatabase(temp, "data/Backups/update/" + name + "/AniMeido.db", version);
        return Path.GetDirectoryName(database)!;
    }

    [Fact]
    public void LaunchUsesTargetMetadataAndRejectsSourceChangedAfterRequest()
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        var database = MakeDatabase(temp, "data/AniMeido.db", 7);
        var source = MakeSource(temp, 8);
        var assembly = temp.PathFor("app-2.2.0/AniMeido.Plugin.Base.dll");
        WriteSchemaAssembly(assembly, 8);
        temp.Manager.RequestRestore(temp.PathFor("data"), source, 8);
        MakeSource(temp, 9);
        var before = Snapshot(temp.PathFor("data"));
        temp.Manager.Launch(() => false, _ => { }, _ => RollbackChoice.Keep, temp.PathFor("data"));
        Assert.False(temp.Store.Read().LastRestore!.Success);
        Assert.Equal(before, Snapshot(temp.PathFor("data")));
        WriteSchemaAssembly(assembly, 9);
        temp.Manager.RequestRestore(temp.PathFor("data"), source, 9);
        temp.Manager.Launch(() => false, _ => { }, _ => RollbackChoice.Keep, temp.PathFor("data"));
        Assert.True(temp.Store.Read().LastRestore!.Success);
        Assert.Equal(9, DatabaseRollback.ReadUserVersion(database));
    }

    [Fact]
    public void ExternallyConflictingPendingStateNeverRestoresData()
    {
        using var temp = new InstallTestRoot();
        temp.AddVersion("2.3.0");
        var state = temp.Save(pending: "2.3.0");
        MakeDatabase(temp, "data/AniMeido.db", 7);
        var source = MakeSource(temp, 8);
        temp.Store.Write(state with
        {
            PendingRestore = new PendingRestoreRequest { Directory = source, RequestedAt = DateTimeOffset.UtcNow },
        });
        var before = Snapshot(temp.PathFor("data"));
        temp.Manager.Launch(() => false, _ => { }, _ => RollbackChoice.Keep, temp.PathFor("data"),
            _ => throw new InvalidOperationException("must reject conflict before reading schema"));
        Assert.Equal(before, Snapshot(temp.PathFor("data")));
        Assert.False(temp.Store.Read().LastRestore!.Success);
        Assert.Contains("先完成更新", temp.Store.Read().LastRestore!.Error);
        Assert.Null(temp.Store.Read().PendingRestore);
        Assert.Equal("2.3.0", temp.Store.Read().Current);
    }

    [Fact]
    public void RequestCannotRaceAnUpdateAlreadyHoldingPreparationLock()
    {
        using var temp = new InstallTestRoot();
        temp.Save();
        var source = MakeSource(temp, 8);
        using (temp.Manager.AcquireUpdateLock())
            Assert.Throws<InstallException>(() => temp.Manager.RequestRestore(temp.PathFor("data"), source, 8));
        Assert.Null(temp.Store.Read().PendingRestore);
    }

    private static string MakeDatabase(InstallTestRoot temp, string relative, int version)
    {
        var bytes = new byte[100];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(60, 4), version);
        var path = temp.PathFor(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => path + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();

    private static void WriteSchemaAssembly(string path, int version)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("SyntheticSchema"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var field = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
            metadata.GetOrAddString("CurrentVersion"), metadata.GetOrAddBlob(new byte[] { 0x06, 0x08 }));
        metadata.AddConstant(field, version);
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("AniMeido.Plugin.Base.Services"),
            metadata.GetOrAddString("DatabaseSchema"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var image = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder());
        var blob = new BlobBuilder();
        image.Serialize(blob);
        File.WriteAllBytes(path, blob.ToArray());
    }
}
