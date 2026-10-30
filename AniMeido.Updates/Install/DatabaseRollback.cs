using System.Text;

namespace AniMeido.Updates.Install;

public sealed record DatabaseRollbackResult(bool Success, string? ArchiveDirectory, string? Error)
{
    public IReadOnlyList<CleanupError> CleanupErrors { get; init; } = [];
}

/// <summary>恢复前调用方必须阻止本安装根下的 App 运行；SQLite 只打开待恢复数据的临时副本。</summary>
public static class DatabaseRollback
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly string[] Suffixes = ["", "-wal", "-shm"];

    public static string GetArchiveDirectory(string dataDirectory, string version)
    {
        SemanticVersion.Parse(version);
        return NewArchiveDirectory(dataDirectory, RollbackDataDirectory.RollbackPrefix + version + "-");
    }

    public static string GetRestoreArchiveDirectory(string dataDirectory)
        => NewArchiveDirectory(dataDirectory, RollbackDataDirectory.RestorePrefix);

    private static string NewArchiveDirectory(string dataDirectory, string prefix)
    {
        var directory = Path.Combine(Path.GetFullPath(dataDirectory), "Backups", "update",
            $"{prefix}{DateTime.Now:yyyyMMdd-HHmmss}");
        return Directory.Exists(directory) || File.Exists(directory)
            ? directory + "-" + Guid.NewGuid().ToString("N") : directory;
    }

    public static int ReadUserVersion(string databasePath)
    {
        var sources = new Dictionary<string, FileStream>();
        try
        {
            OpenSources(databasePath, includeSidecars: true, sources);
            return DatabaseSnapshotVersion.Read(sources);
        }
        finally
        {
            foreach (var source in sources.Values)
                source.Dispose();
        }
    }

    public static string ValidateRestore(string dataDirectory, string directory, int currentVersion)
    {
        var full = RollbackDataDirectory.Validate(Path.Combine(dataDirectory, "Backups", "update"), directory);
        CheckVersion(ReadUserVersion(Path.Combine(full, "AniMeido.db")), currentVersion);
        return full;
    }

    private static void CheckVersion(int version, int currentVersion)
    {
        if (version > currentVersion)
            throw new InstallException("这份数据来自更新的版本，请先升级 AniMeido。");
    }

    public static DatabaseRollbackResult Restore(string dataDirectory, string backupPath, string archiveDirectory)
        => RestoreCore(dataDirectory, backupPath, archiveDirectory, restoreSidecars: false, currentVersion: null);

    public static DatabaseRollbackResult RestoreArchived(string dataDirectory, string directory,
        string archiveDirectory, int currentVersion)
        => RestoreArchived(dataDirectory, directory, archiveDirectory, currentVersion, null);

    internal static DatabaseRollbackResult RestoreArchived(string dataDirectory, string directory,
        string archiveDirectory, int currentVersion, Action<string>? beforeInstall)
    {
        var full = RollbackDataDirectory.Validate(Path.Combine(dataDirectory, "Backups", "update"), directory);
        return RestoreCore(dataDirectory, Path.Combine(full, "AniMeido.db"), archiveDirectory,
            restoreSidecars: true, currentVersion, beforeInstall);
    }

    private static DatabaseRollbackResult RestoreCore(string dataDirectory, string backupPath,
        string archiveDirectory, bool restoreSidecars, int? currentVersion, Action<string>? beforeInstall = null)
    {
        var data = Path.GetFullPath(dataDirectory);
        var database = Path.Combine(data, "AniMeido.db");
        var archive = Path.GetFullPath(archiveDirectory);
        var archiveRoot = Path.Combine(data, "Backups", "update");
        var prefix = restoreSidecars ? RollbackDataDirectory.RestorePrefix : RollbackDataDirectory.RollbackPrefix;
        if (!string.Equals(Path.GetDirectoryName(archive), archiveRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(archive).StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException("另存目录必须位于数据目录的 Backups/update 中。", nameof(archiveDirectory));

        var staging = archive + ".tmp-" + Guid.NewGuid().ToString("N");
        var sources = new Dictionary<string, FileStream>();
        var temporaries = new List<string>();
        var detachedFiles = new List<(string Original, string Detached)>();
        var installedFiles = new List<string>();
        var cleanupErrors = new List<CleanupError>();
        DatabaseRollbackResult? result = null;
        var archived = false;
        var stagingCreated = false;
        try
        {
            // 版本判断与实际恢复使用同一组源句柄；不在检查后重新打开源文件。
            OpenSources(backupPath, restoreSidecars, sources);
            if (currentVersion is { } version)
                CheckVersion(DatabaseSnapshotVersion.Read(sources), version);
            if (!File.Exists(database))
                throw new InstallException("当前 AniMeido.db 不存在，无法另存当前数据。");
            RollbackDataDirectory.CheckAncestors(archiveRoot);
            Directory.CreateDirectory(staging);
            stagingCreated = true;
            foreach (var suffix in Suffixes)
            {
                RollbackDataDirectory.CheckAncestors(database + suffix);
                try
                {
                    using var input = new FileStream(database + suffix, FileMode.Open, FileAccess.Read, FileShare.Read);
                    CopyDurably(input, Path.Combine(staging, "AniMeido.db" + suffix));
                }
                catch (FileNotFoundException) when (suffix.Length != 0) { }
            }
            Directory.Move(staging, archive);
            archived = true;

            foreach (var (suffix, input) in sources)
            {
                input.Position = 0;
                var temporary = database + ".restore-tmp" + suffix;
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                temporaries.Add(temporary);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            // 主库与 sidecar 都暂时移走；任何一步失败都能放回，且不覆盖意外出现的文件。
            foreach (var suffix in Suffixes.Skip(1).Append(""))
            {
                var original = database + suffix;
                var detached = Path.Combine(archive, "AniMeido.db" + suffix + ".detached");
                try
                {
                    File.Move(original, detached);
                    detachedFiles.Add((original, detached));
                }
                catch (FileNotFoundException) when (suffix.Length != 0) { }
            }
            foreach (var suffix in Suffixes.Where(sources.ContainsKey))
            {
                var target = database + suffix;
                beforeInstall?.Invoke(suffix);
                File.Move(database + ".restore-tmp" + suffix, target);
                installedFiles.Add(target);
            }
            result = new(true, archive, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = ex.Message;
            var recoveryFailed = false;
            foreach (var installed in installedFiles.AsEnumerable().Reverse())
            {
                try
                {
                    File.Delete(installed);
                }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException)
                {
                    recoveryFailed = true;
                    error += $"\n无法移除未完成的恢复文件 {installed}：{recoveryError.Message}";
                }
            }
            foreach (var (original, detached) in detachedFiles.AsEnumerable().Reverse())
            {
                try
                {
                    File.Move(detached, original);
                }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException)
                {
                    recoveryFailed = true;
                    error += $"\n无法放回 {original}，原文件保留在 {detached}：{recoveryError.Message}";
                }
            }
            result = new(false, archived ? archive : null,
                archived && !recoveryFailed ? $"{error}\n当前数据库未变。" : error);
        }
        finally
        {
            foreach (var input in sources.Values)
                input.Dispose();
            if (result?.Success == true)
            {
                foreach (var (_, detached) in detachedFiles)
                {
                    TryDeleteTemporary(detached, isDirectory: false, cleanupErrors);
                }
            }
            if (stagingCreated)
                TryDeleteTemporary(staging, isDirectory: true, cleanupErrors);
            foreach (var temporary in temporaries)
                TryDeleteTemporary(temporary, isDirectory: false, cleanupErrors);
        }

        return result with { CleanupErrors = cleanupErrors.AsReadOnly() };
    }

    private static void OpenSources(string databasePath, bool includeSidecars, Dictionary<string, FileStream> sources)
    {
        RollbackDataDirectory.CheckAncestors(Path.GetFullPath(databasePath));
        var database = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        sources.Add("", database);
        Span<byte> header = stackalloc byte[16];
        database.ReadExactly(header);
        if (!header.SequenceEqual(SqliteHeader))
            throw new InstallException("这份数据不是有效的 SQLite 文件（文件头错误）。");

        if (!includeSidecars)
            return;

        foreach (var suffix in Suffixes.Skip(1))
        {
            RollbackDataDirectory.CheckAncestors(databasePath + suffix);
            try
            {
                sources.Add(suffix, new FileStream(databasePath + suffix, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            catch (FileNotFoundException)
            {
                // 归档可以没有 WAL 或 SHM，只有实际存在的文件参与恢复。
            }
        }
    }

    private static void CopyDurably(Stream input, string target)
    {
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void TryDeleteTemporary(string path, bool isDirectory, List<CleanupError> errors)
    {
        try
        {
            if (isDirectory)
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            else
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new CleanupError(path, ex.Message));
        }
    }
}
