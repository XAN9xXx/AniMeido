using System.Text;

namespace AniMeido.Updates.Install;

public sealed record DatabaseRollbackResult(bool Success, string? ArchiveDirectory, string? Error);

/// <summary>调用方必须先阻止本安装根下的 App 运行；本类不访问环境变量、不打开 SQLite。</summary>
public static class DatabaseRollback
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    public static string GetArchiveDirectory(string dataDirectory, string version)
    {
        SemanticVersion.Parse(version);
        var directory = Path.Combine(Path.GetFullPath(dataDirectory), "Backups", "update",
            $"AniMeido-rollback-from-{version}-{DateTime.Now:yyyyMMdd-HHmmss}");
        return Directory.Exists(directory) || File.Exists(directory)
            ? directory + "-" + Guid.NewGuid().ToString("N") : directory;
    }

    public static DatabaseRollbackResult Restore(string dataDirectory, string backupPath, string archiveDirectory)
    {
        var data = Path.GetFullPath(dataDirectory);
        var database = Path.Combine(data, "AniMeido.db");
        var archive = Path.GetFullPath(archiveDirectory);
        var archiveRoot = Path.Combine(data, "Backups", "update") + Path.DirectorySeparatorChar;
        if (!archive.StartsWith(archiveRoot, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(archive) != Path.TrimEndingDirectorySeparator(archiveRoot)
            || !Path.GetFileName(archive).StartsWith("AniMeido-rollback-from-", StringComparison.Ordinal))
        {
            throw new ArgumentException("回滚另存目录必须位于数据目录的 Backups/update 中。", nameof(archiveDirectory));
        }

        var staging = archive + ".tmp-" + Guid.NewGuid().ToString("N");
        var restoreTemporary = database + ".restore-tmp";
        var archived = false;
        var stagingCreated = false;
        var restoreTemporaryCreated = false;
        try
        {
            // 无效备份不能改动任何文件，因此在另存之前先校验，且持有只读句柄直到复制完成。
            using var backup = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[16];
            if (backup.Read(header) != SqliteHeader.Length || !header.SequenceEqual(SqliteHeader))
            {
                return new DatabaseRollbackResult(false, null, "迁移前备份不是有效的 SQLite 文件（文件头错误）。");
            }
            if (!File.Exists(database))
            {
                return new DatabaseRollbackResult(false, null, "当前 AniMeido.db 不存在，无法另存回滚前数据。");
            }

            Directory.CreateDirectory(staging);
            stagingCreated = true;
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    CopyDurably(database + suffix, Path.Combine(staging, "AniMeido.db" + suffix));
                }
                catch (FileNotFoundException) when (suffix.Length != 0)
                {
                    // sidecar 可以不存在；不能把访问被拒绝等错误当作“不存在”忽略。
                }
            }
            Directory.Move(staging, archive);
            archived = true;

            backup.Position = 0;
            using (var target = new FileStream(restoreTemporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                restoreTemporaryCreated = true;
                backup.CopyTo(target);
                target.Flush(flushToDisk: true);
            }
            File.Delete(database + "-wal");
            File.Delete(database + "-shm");
            File.Move(restoreTemporary, database, overwrite: true);
            return new DatabaseRollbackResult(true, archive, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 已完成的另存目录永不删除；恢复中止，InstallManager 不提交回滚状态。
            return new DatabaseRollbackResult(false, archived ? archive : null, ex.Message);
        }
        finally
        {
            if (stagingCreated)
            {
                TryDeleteTemporary(staging, isDirectory: true);
            }
            if (restoreTemporaryCreated)
            {
                TryDeleteTemporary(restoreTemporary, isDirectory: false);
            }
        }
    }

    private static void CopyDurably(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
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
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保留失败的临时文件，不覆盖原始失败原因。
        }
    }
}
