using System.Globalization;
using AniMeido.Contracts;
using AniMeido.Updates;
using AniMeido.Updates.Install;
using Serilog;

namespace AniMeido.App.Services;

internal sealed record RollbackDataEntry(string Path, string Version, DateTime Time, bool IsBeforeRestore = false)
{
    public string DisplayText => IsBeforeRestore ? $"恢复前数据 | 时间：{Time:yyyy-MM-dd HH:mm:ss}"
        : $"回滚前数据 | 来源版本：{Version} | 时间：{Time:yyyy-MM-dd HH:mm:ss}";
}

/// <summary>只管理回滚/恢复时另存的数据；不直接修改当前数据库。</summary>
internal sealed class RollbackDataStore(IAppDataPaths paths)
{
    private const string Prefix = RollbackDataDirectory.RollbackPrefix;
    private readonly string _root = Path.GetFullPath(Path.Combine(paths.BackupDirectory, "update"));

    public IReadOnlyList<RollbackDataEntry> List()
    {
        try
        {
            if (!Directory.Exists(_root))
                return [];
            RollbackDataDirectory.CheckAncestors(_root);
            return Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly)
                .Where(directory => RollbackDataDirectory.IsArchiveName(Path.GetFileName(directory)))
                .Select(ReadEntry)
                .OrderByDescending(entry => entry.Time).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "无法列出回滚前数据：{Root}", _root);
            throw;
        }
    }

    public string ValidateDirectory(string directory)
        => RollbackDataDirectory.Validate(_root, directory);

    public void Delete(string directory)
    {
        try
        {
            var full = ValidateDirectory(directory);
            CheckTree(full);
            Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warning(ex, "删除回滚前数据失败：{Path}", directory);
            throw;
        }
    }

    private RollbackDataEntry ReadEntry(string directory)
    {
        var full = ValidateDirectory(directory);
        var fileName = Path.GetFileName(full);
        if (fileName.StartsWith(RollbackDataDirectory.RestorePrefix, StringComparison.Ordinal))
        {
            var suffix = fileName[RollbackDataDirectory.RestorePrefix.Length..];
            var time = suffix.Length >= 15 && DateTime.TryParseExact(suffix[..15], "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed : Directory.GetCreationTime(full);
            return new(full, "", time, IsBeforeRestore: true);
        }
        var name = fileName[Prefix.Length..];
        // 目录后缀：<SemVer>-yyyyMMdd-HHmmss[-碰撞时的 GUID]；预览版号自身可以含 '-'。
        for (var i = 0; i + 16 <= name.Length; i++)
        {
            if (name[i] == '-' && DateTime.TryParseExact(name.Substring(i + 1, 15), "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                && SemanticVersion.TryParse(name[..i], out _)
                && (i + 16 == name.Length || name[i + 16] == '-'))
                return new(full, name[..i], time);
        }
        return new(full, name, Directory.GetCreationTime(full));
    }

    private static void CheckTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"拒绝删除包含符号链接或 junction 的目录：{entry}");
            if (Directory.Exists(entry))
                CheckTree(entry);
        }
    }
}
