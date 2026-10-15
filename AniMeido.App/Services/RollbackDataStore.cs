using System.Globalization;
using AniMeido.Contracts;
using AniMeido.Updates;
using Serilog;

namespace AniMeido.App.Services;

internal sealed record RollbackDataEntry(string Path, string Version, DateTime Time)
{
    public string DisplayText => $"来源版本：{Version} | 时间：{Time:yyyy-MM-dd HH:mm:ss}";
}

/// <summary>只管理版本回滚时另存的数据；没有恢复或修改当前数据库的能力。</summary>
internal sealed class RollbackDataStore(IAppDataPaths paths)
{
    private const string Prefix = "AniMeido-rollback-from-";
    private readonly string _root = Path.GetFullPath(Path.Combine(paths.BackupDirectory, "update"));

    public IReadOnlyList<RollbackDataEntry> List()
    {
        try
        {
            if (!Directory.Exists(_root))
                return [];
            CheckAncestors(_root);
            return Directory.EnumerateDirectories(_root, Prefix + "*", SearchOption.TopDirectoryOnly)
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
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetDirectoryName(full), _root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith(Prefix, StringComparison.Ordinal)
            || !Directory.Exists(full))
            throw new IOException("只能管理 Backups\\update\\AniMeido-rollback-from-* 目录。");
        CheckAncestors(full);
        return full;
    }

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
        var name = Path.GetFileName(full)[Prefix.Length..];
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

    private static void CheckAncestors(string path)
    {
        for (string? entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"拒绝操作符号链接或 junction：{entry}");
        }
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
