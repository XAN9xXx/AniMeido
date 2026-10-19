namespace AniMeido.Updates.Install;

/// <summary>App 和启动器共用的保留数据目录边界。</summary>
public static class RollbackDataDirectory
{
    public const string RollbackPrefix = "AniMeido-rollback-from-";
    public const string RestorePrefix = "AniMeido-before-restore-";

    public static bool IsArchiveName(string name) => name.StartsWith(RollbackPrefix, StringComparison.Ordinal)
        || name.StartsWith(RestorePrefix, StringComparison.Ordinal);

    public static string Validate(string updateRoot, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(updateRoot));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)
            || !IsArchiveName(Path.GetFileName(full)) || !Directory.Exists(full))
            throw new IOException("只能管理 Backups\\update 下的回滚前数据或恢复前数据目录。");
        CheckAncestors(full);
        return full;
    }

    public static void CheckAncestors(string path)
    {
        for (string? entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
        {
            try
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"拒绝操作符号链接或 junction：{entry}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
