namespace AniMeido.Updates.Install;

public sealed record CleanupError(string Path, string Message);

public sealed class InstallManager(InstallStateStore store, Action<string>? log = null)
{
    public InstallState ReadState() => store.Read();

    /// <summary>已有 App 正在运行时，只读正式状态；不恢复状态，也不删除 tmp。</summary>
    public string GetCurrentExecutableReadOnly() => store.WithLock(() =>
        InstallLayout.ExecutablePath(store.Root, store.ReadExistingLocked().Current));

    public LaunchDecision PrepareLaunch(Func<RollbackRequest, RollbackChoice>? choose = null,
        string? dataDirectory = null, Func<bool>? noAppRunning = null)
        => store.WithLock(() => PrepareLaunchLocked(choose, dataDirectory, noAppRunning));

    /// <summary>持锁期间复查进程并创建子进程，避免两个启动器在进程尚未创建的间隙重复计数。</summary>
    public string Launch(Func<bool> hasRunningApp, Action<string> start,
        Func<RollbackRequest, RollbackChoice> choose, string dataDirectory) => store.WithLock(() =>
    {
        string current;
        if (hasRunningApp())
        {
            current = store.ReadExistingLocked().Current;
            log?.Invoke("本安装根下已有 App 运行，按 current 多开，不更改安装状态。");
        }
        else
        {
            var decision = PrepareLaunchLocked(choose, dataDirectory, () => !hasRunningApp());
            current = decision.State.Current;
        }
        var executable = InstallLayout.ExecutablePath(store.Root, current);
        start(executable);
        return executable;
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
        ChangeCurrent(version, state => state with
        {
            Migration = new MigrationRecord { Version = version, Backup = backup },
        });
    }

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

    public IReadOnlyList<CleanupError> CleanupObsolete()
    {
        var errors = new List<CleanupError>();
        try
        {
            store.WithLock(() =>
            {
                var state = store.ReadLocked();
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app-" + state.Current };
                if (state.Previous is not null)
                {
                    keep.Add("app-" + state.Previous);
                }
                // U3 可能刚设 pending；不能在后台清理中删除下一次启动需要的目录。
                if (state.Pending is not null)
                {
                    keep.Add("app-" + state.Pending);
                }
                foreach (var directory in Directory.GetDirectories(store.Root))
                {
                    var name = Path.GetFileName(directory);
                    if (!keep.Contains(name) && (name.StartsWith("app-", StringComparison.Ordinal)
                        || name.EndsWith(".staging", StringComparison.OrdinalIgnoreCase)))
                    {
                        TryCleanup(directory, () => DeleteDirectorySafely(directory), errors);
                    }
                }
                var packages = Path.Combine(store.Root, "packages");
                if (Directory.Exists(packages))
                {
                    TryCleanup(packages, () =>
                    {
                        RejectReparsePoint(packages);
                        foreach (var file in Directory.GetFiles(packages))
                        {
                            // U3 需把 U1 的 hash 文件名关联到版本；这里约定版本化包名（及 .partial）。
                            var name = Path.GetFileName(file);
                            var pendingPackage = state.Pending is null ? null : $"AniMeido-{state.Pending}-win-x64.zip";
                            if (pendingPackage is null || (name != pendingPackage && name != pendingPackage + ".partial"))
                            {
                                TryCleanup(file, () => File.Delete(file), errors);
                            }
                        }
                    }, errors);
                }
                return true;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new CleanupError(store.Root, ex.Message));
        }
        return errors;
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
