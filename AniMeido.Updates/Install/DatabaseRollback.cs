using System.Buffers.Binary;
using System.Text;

namespace AniMeido.Updates.Install;

public sealed record DatabaseRollbackResult(bool Success, string? ArchiveDirectory, string? Error);

/// <summary>调用方必须先阻止本安装根下的 App 运行；本类不访问环境变量、不打开 SQLite。</summary>
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
        RollbackDataDirectory.CheckAncestors(Path.GetFullPath(databasePath));
        using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadUserVersion(stream);
    }

    private static int ReadUserVersion(Stream stream)
    {
        Span<byte> header = stackalloc byte[64];
        stream.ReadExactly(header);
        if (!header[..16].SequenceEqual(SqliteHeader))
            throw new InstallException("这份数据不是有效的 SQLite 文件（文件头错误）。");
        var version = BinaryPrimitives.ReadInt32BigEndian(header[60..64]);
        if (version < 0)
            throw new InstallException("这份数据的 SQLite user_version 无效。");
        return version;
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
        var archived = false;
        var stagingCreated = false;
        var completed = false;
        try
        {
            // 校验并持有源文件只读句柄，直到复制完成；另存之前不改动当前库。
            RollbackDataDirectory.CheckAncestors(backupPath);
            var backup = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            sources.Add("", backup);
            if (currentVersion is { } version)
                CheckVersion(ReadUserVersion(backup), version);
            else
            {
                Span<byte> header = stackalloc byte[16];
                backup.ReadExactly(header);
                if (!header.SequenceEqual(SqliteHeader))
                    throw new InstallException("迁移前备份不是有效的 SQLite 文件（文件头错误）。");
            }
            if (restoreSidecars)
            {
                foreach (var suffix in Suffixes.Skip(1))
                {
                    RollbackDataDirectory.CheckAncestors(backupPath + suffix);
                    try
                    {
                        sources.Add(suffix, new FileStream(backupPath + suffix, FileMode.Open, FileAccess.Read, FileShare.Read));
                    }
                    catch (FileNotFoundException) { }
                }
            }
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
            completed = true;
            return new(true, archive, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = ex.Message;
            var recoveryFailed = false;
            foreach (var installed in installedFiles.AsEnumerable().Reverse())
            {
                try { File.Delete(installed); }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException)
                {
                    recoveryFailed = true;
                    error += $"\n无法移除未完成的恢复文件 {installed}：{recoveryError.Message}";
                }
            }
            foreach (var (original, detached) in detachedFiles.AsEnumerable().Reverse())
            {
                try { File.Move(detached, original); }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException)
                {
                    recoveryFailed = true;
                    error += $"\n无法放回 {original}，原文件保留在 {detached}：{recoveryError.Message}";
                }
            }
            return new(false, archived ? archive : null,
                archived && !recoveryFailed ? $"{error}\n当前数据库未变。" : error);
        }
        finally
        {
            foreach (var input in sources.Values)
                input.Dispose();
            if (completed)
                foreach (var (_, detached) in detachedFiles)
                    TryDeleteTemporary(detached, isDirectory: false);
            if (stagingCreated)
                TryDeleteTemporary(staging, isDirectory: true);
            foreach (var temporary in temporaries)
                TryDeleteTemporary(temporary, isDirectory: false);
        }
    }

    private static void CopyDurably(Stream input, string target)
    {
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void TryDeleteTemporary(string path, bool isDirectory)
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
            // 保留失败的临时文件，不覆盖原始失败原因。
        }
    }
}
