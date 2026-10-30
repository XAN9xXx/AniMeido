using Microsoft.Data.Sqlite;

namespace AniMeido.Updates.Install;

/// <summary>只在临时副本上打开 SQLite，让引擎解释已提交但尚未合并的 WAL。</summary>
internal static class DatabaseSnapshotVersion
{
    public static int Read(IReadOnlyDictionary<string, FileStream> sources)
    {
        // 保留版本读取原有的文件头长度要求；SQLite 会将部分短文件视为空库。
        if (sources[""].Length < 64)
            throw new EndOfStreamException("数据库文件头不完整。");

        var directory = Directory.CreateTempSubdirectory("AniMeido-restore-version-");
        try
        {
            var database = Path.Combine(directory.FullName, "AniMeido.db");
            Copy(sources[""], database);
            if (sources.TryGetValue("-wal", out var wal))
                Copy(wal, database + "-wal");

            // SHM 是可重建的索引，不复制源文件；所有重建只发生在临时目录。
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = checked((int)(long)command.ExecuteScalar()!);
            if (version < 0)
                throw new InstallException("这份数据的 SQLite user_version 无效。");
            return version;
        }
        catch (SqliteException ex)
        {
            throw new InstallException("无法读取这份数据库的版本，未执行恢复。", ex);
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 不让临时文件清理覆盖读取结果或原始失败。
                System.Diagnostics.Trace.TraceWarning("无法清理数据库版本读取临时目录 {0}：{1}", directory.FullName, ex);
            }
        }
    }

    private static void Copy(FileStream source, string destination)
    {
        source.Position = 0;
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(output);
    }
}
