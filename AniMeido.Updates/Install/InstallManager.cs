namespace AniMeido.Updates.Install;

public sealed record CleanupError(string Path, string Message);

public sealed class InstallManager(InstallStateStore store, Action<string>? log = null)
{
    public InstallState ReadState() => store.Read();

    /// <summary>
    /// 下载、解压到登记 pending 全程与清理共用此跨进程锁。
    /// 先取得此锁，再访问 state.lock；不在网络请求期间占用状态锁。
    /// </summary>
    public IDisposable AcquireUpdateLock()
    {
        try
        {
            return new FileStream(Path.Combine(store.Root, "update.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InstallException("无法取得 update.lock；另一个实例可能正在准备更新或清理，请稍后重试。", ex);
        }
    }

    /// <summary>已有 App 正在运行时，只读正式状态；不恢复状态，也不删除 tmp。</summary>
    public string GetCurrentExecutableReadOnly() => store.WithLock(() =>
        InstallLayout.ExecutablePath(store.Root, store.ReadExistingLocked().Current));

    public LaunchDecision PrepareLaunch(Func<RollbackRequest, RollbackChoice>? choose = null,
        string? dataDirectory = null, Func<bool>? noAppRunning = null)
        => store.WithLock(() => PrepareLaunchLocked(choose, dataDirectory, noAppRunning));

    /// <summary>持锁期间复查进程并创建子进程，避免两个启动器在进程尚未创建的间隙重复计数。</summary>
    public string Launch(Func<bool> hasRunningApp, Action<string> start,
        Func<RollbackRequest, RollbackChoice> choose, string dataDirectory,
        Func<string, int>? readDatabaseVersion = null) => store.WithLock(() =>
    {
        string current;
        if (hasRunningApp())
        {
            current = store.ReadExistingLocked().Current;
            log?.Invoke("本安装根下已有 App 运行，按 current 多开，不更改安装状态。");
        }
        else
        {
            RestorePendingLocked(dataDirectory, () => !hasRunningApp(), readDatabaseVersion ?? InstalledDatabaseVersion.Read);
            var decision = PrepareLaunchLocked(choose, dataDirectory, () => !hasRunningApp());
            current = decision.State.Current;
        }
        var executable = InstallLayout.ExecutablePath(store.Root, current);
        start(executable);
        return executable;
    });

    private void RestorePendingLocked(string dataDirectory, Func<bool> noAppRunning,
        Func<string, int> readDatabaseVersion)
    {
        var state = store.ReadLocked();
        if (state.PendingRestore is not { } request || !noAppRunning())
            return;
        DatabaseRollbackResult result;
        try
        {
            if (state.Pending is not null)
                throw new InstallException("有待切换版本，请先完成更新，未恢复数据库。");
            var version = readDatabaseVersion(InstallLayout.ExecutablePath(store.Root, state.Current));
            var archive = DatabaseRollback.GetRestoreArchiveDirectory(dataDirectory);
            result = DatabaseRollback.RestoreArchived(dataDirectory, request.Directory, archive, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or BadImageFormatException or InvalidOperationException)
        {
            result = new(false, null, ex.Message);
        }
        store.WriteLocked(state with
        {
            PendingRestore = null,
            LastRestore = new RestoreNotice
            {
                Directory = request.Directory, Archive = result.ArchiveDirectory,
                Success = result.Success, Error = result.Error, At = DateTimeOffset.UtcNow,
            },
            // 新恢复的数据应先获得完整的启动机会，不能沿用旧库的失败计数立即回滚。
            CurrentHealthy = result.Success ? false : state.CurrentHealthy,
            FailedStarts = result.Success ? 0 : state.FailedStarts,
            Migration = result.Success ? null : state.Migration,
        });
        ReportRestoreCleanupErrors(result);
        log?.Invoke(result.Success ? $"已从 {request.Directory} 恢复，当前数据另存到 {result.ArchiveDirectory}。"
            : $"恢复数据失败：{result.Error}");
    }

    internal void ReportRestoreCleanupErrors(DatabaseRollbackResult result)
    {
        foreach (var error in result.CleanupErrors)
        {
            log?.Invoke($"恢复流程的临时文件未能清理，已保留 {error.Path}：{error.Message}");
        }
    }

    public string ValidateRestore(string dataDirectory, string directory, int currentVersion) => store.WithLock(() =>
    {
        EnsureCanRestore(store.ReadLocked());
        return DatabaseRollback.ValidateRestore(dataDirectory, directory, currentVersion);
    });

    public void RequestRestore(string dataDirectory, string directory, int currentVersion)
    {
        // 与准备更新的下载/解压阶段互斥，避免恢复请求写入后才发现有更新正在准备。
        using var updateLock = AcquireUpdateLock();
        store.WithLock(() =>
        {
            var state = store.ReadLocked();
            EnsureCanRestore(state);
            var full = DatabaseRollback.ValidateRestore(dataDirectory, directory, currentVersion);
            store.WriteLocked(state with
            {
                PendingRestore = new PendingRestoreRequest { Directory = full, RequestedAt = DateTimeOffset.UtcNow },
            });
            return true;
        });
    }

    private static void EnsureCanRestore(InstallState state)
    {
        if (state.Pending is not null)
            throw new InstallException("有待切换版本，请先完成更新再恢复数据。");
        if (state.PendingRestore is not null)
            throw new InstallException("已有待恢复数据，请先重启 AniMeido 完成恢复。");
    }

    public RestoreNotice? ReadRestoreNotice() => store.Read().LastRestore;

    /// <summary>App 启动时仍存在的请求说明启动器没有处理它（例如旧版启动器不认识该字段）。</summary>
    public PendingRestoreRequest? ReadPendingRestore() => store.Read().PendingRestore;

    public void ClearPendingRestore(PendingRestoreRequest request) => store.WithLock(() =>
    {
        var state = store.ReadLocked();
        if (state.PendingRestore == request)
            store.WriteLocked(state with { PendingRestore = null });
        return true;
    });

    public void ClearRestoreNotice(RestoreNotice notice) => store.WithLock(() =>
    {
        var state = store.ReadLocked();
        if (state.LastRestore == notice)
            store.WriteLocked(state with { LastRestore = null });
        return true;
    });

    private LaunchDecision PrepareLaunchLocked(Func<RollbackRequest, RollbackChoice>? choose,
        string? dataDirectory, Func<bool>? noAppRunning)
    {
        var original = store.ReadLocked();
        bool IsValid(string version) => InstallLayout.IsValidVersion(store.Root, version);
        var decision = LaunchDecisions.Evaluate(original, IsValid);
        if (decision.NeedsRollbackChoice && choose is not null)
        {
            if (dataDirectory is null)
            {
                throw new ArgumentException("数据库回滚必须提供数据目录。", nameof(dataDirectory));
            }
            var state = decision.State;
            var archive = DatabaseRollback.GetArchiveDirectory(dataDirectory, state.Current);
            var request = new RollbackRequest(state.Current, state.Previous!, state.Migration!.Backup, archive);
            var choice = choose(request);
            if (choice == RollbackChoice.Restore)
            {
                if (noAppRunning?.Invoke() != true)
                {
                    throw new InstallException("仍有 AniMeido.App.exe 正在运行或无法确认其状态；未恢复数据库，未切换版本。");
                }
                var result = DatabaseRollback.Restore(dataDirectory, request.Backup, archive);
                ReportRestoreCleanupErrors(result);
                if (!result.Success)
                {
                    throw new InstallException($"恢复数据库失败，未切换版本：{result.Error}"
                        + (result.ArchiveDirectory is null ? "" : $"\n回滚前数据已另存到：{result.ArchiveDirectory}"));
                }
                log?.Invoke($"回滚前数据已另存到 {result.ArchiveDirectory}。");
            }
            decision = LaunchDecisions.Evaluate(state, IsValid, choice);
        }
        foreach (var message in decision.Messages)
        {
            log?.Invoke(message);
        }
        store.WriteLocked(decision.State);
        return decision;
    }

    public void MarkHealthy(string version) => ChangeCurrent(version, state => state with
    {
        CurrentHealthy = true,
        FailedStarts = 0,
        Migration = null,
    });

    public void RecordMigration(string version, string backupPath)
    {
        var backup = Path.GetFullPath(backupPath);
        SemanticVersion.Parse(version);
        store.WithLock(() =>
        {
            var state = store.ReadLocked();
            if (!string.Equals(version, state.Current, StringComparison.Ordinal))
                throw new InstallException("运行版本与当前安装版本不一致，未登记数据库升级。");

            store.WriteLocked(state with
            {
                Migration = new MigrationRecord { Version = version, Backup = backup },
            });
            return true;
        });
    }

    public RolledBackNotice? ReadRolledBackNotice() => store.Read().RolledBackFrom;

    public void ClearRolledBackNotice(RolledBackNotice notice) => store.WithLock(() =>
    {
        var state = store.ReadLocked();
        if (state.RolledBackFrom == notice)
            store.WriteLocked(state with { RolledBackFrom = null });
        return true;
    });

    public RolledBackNotice? ConsumeRolledBackNotice() => store.WithLock(() =>
    {
        var state = store.ReadLocked();
        if (state.RolledBackFrom is not { } notice)
        {
            return null;
        }
        store.WriteLocked(state with { RolledBackFrom = null });
        return notice;
    });

    public void SetPending(string version) => store.WithLock(() =>
    {
        var pending = SemanticVersion.Parse(version);
        var state = store.ReadLocked();
        if (state.PendingRestore is not null)
            throw new InstallException("已有待恢复数据，请先重启完成恢复再更新。");
        if (!InstallLayout.IsValidVersion(store.Root, version) || pending.CompareTo(SemanticVersion.Parse(state.Current)) <= 0)
        {
            throw new InstallException("待切换版本必须是有效的版本目录，且版本号高于 current。");
        }
        store.WriteLocked(state with { Pending = version });
        return true;
    });

    private void ChangeCurrent(string version, Func<InstallState, InstallState> change) => store.WithLock(() =>
    {
        SemanticVersion.Parse(version);
        var state = store.ReadLocked();
        if (string.Equals(version, state.Current, StringComparison.Ordinal))
        {
            store.WriteLocked(change(state));
        }
        return true;
    });

    public IReadOnlyList<CleanupError> CleanupObsolete() => CleanupObsolete(null);

    internal IReadOnlyList<CleanupError> CleanupObsolete(Action<string>? beforeDirectoryDelete)
    {
        var errors = new List<CleanupError>();
        try
        {
            using var updateLock = AcquireUpdateLock();
            var directories = store.WithLock(() => StageObsoleteDirectories(errors));
            foreach (var directory in directories)
            {
                TryCleanup(directory.Original, () =>
                {
                    beforeDirectoryDelete?.Invoke(directory.Staged);
                    DeleteDirectorySafely(directory.Staged);
                }, errors);
            }
            CleanupPackages(errors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new CleanupError(store.Root, ex.Message));
        }
        return errors;
    }

    private List<(string Original, string Staged)> StageObsoleteDirectories(List<CleanupError> errors)
    {
        var state = store.ReadLocked();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app-" + state.Current };
        if (state.Previous is not null)
            keep.Add("app-" + state.Previous);
        if (state.Pending is not null)
            keep.Add("app-" + state.Pending);

        var staged = new List<(string Original, string Staged)>();
        foreach (var directory in Directory.GetDirectories(store.Root))
        {
            var name = Path.GetFileName(directory);
            if (keep.Contains(name) || (!name.StartsWith("app-", StringComparison.Ordinal)
                && !name.EndsWith(".staging", StringComparison.OrdinalIgnoreCase)))
                continue;

            TryCleanup(directory, () =>
            {
                RejectReparsePoint(directory);
                // 锁内改名后，启动恢复和 SetPending 都不能再把它选作版本。
                // 失败或中断后仍按既有 staging 规则重试，不新增安装状态字段。
                var destination = Path.Combine(store.Root, $".cleanup-{Guid.NewGuid():N}.staging");
                Directory.Move(directory, destination);
                staged.Add((directory, destination));
            }, errors);
        }
        return staged;
    }

    private void CleanupPackages(List<CleanupError> errors)
    {
        var packages = Path.Combine(store.Root, "packages");
        if (!Directory.Exists(packages))
            return;

        TryCleanup(packages, () =>
        {
            RejectReparsePoint(packages);
            foreach (var file in Directory.GetFiles(packages))
            {
                // update.lock 仍由整次清理持有，下载器不会同时操作包文件。
                var extension = Path.GetExtension(file);
                if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                    || (extension.Equals(".partial", StringComparison.OrdinalIgnoreCase)
                        && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)))
                    TryCleanup(file, () => File.Delete(file), errors);
            }
        }, errors);
    }

    private void DeleteDirectorySafely(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), store.Root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallException("拒绝清理安装根以外的目录。");
        }
        // 递归删除前先检查子树，不穿过符号链接/junction 删除其他位置。
        RejectReparsePoint(full);
        CheckTree(full);
        Directory.Delete(full, recursive: true);
    }

    private static void CheckTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectReparsePoint(entry);
            if (Directory.Exists(entry))
            {
                CheckTree(entry);
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InstallException($"拒绝清理符号链接或 junction：{path}");
        }
    }

    private static void TryCleanup(string path, Action action, List<CleanupError> errors)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new CleanupError(path, ex.Message));
        }
    }
}
