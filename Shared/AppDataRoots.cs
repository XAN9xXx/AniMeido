using System;
using System.Collections.Generic;
using System.IO;

namespace AniMeido;

internal static class AppDataRoots
{
    internal const string EnvironmentVariable = "ANIMEIDO_DATA_ROOT";

    internal static string Roaming => GetRoot("Roaming", Environment.SpecialFolder.ApplicationData);
    internal static string Local => GetRoot("Local", Environment.SpecialFolder.LocalApplicationData);
    internal static string Pictures => GetRoot("Pictures", Environment.SpecialFolder.MyPictures);
    internal static bool IsIsolated => IsolatedRoot is not null;

    internal static string? IsolatedRoot
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrEmpty(value) ? null : ValidateIsolatedRoot(value);
        }
    }

    internal static string DisplaySuffix
    {
        get
        {
            if (IsolatedRoot is not { } root)
                return "";
            var name = Path.GetFileName(root);
            return $"（独立数据：{(name.Length > 0 ? name : root)}）";
        }
    }

    private static string GetRoot(string directory, Environment.SpecialFolder fallback)
        => IsolatedRoot is { } root
            ? Path.Combine(root, directory)
            : Path.Combine(Environment.GetFolderPath(fallback), "AniMeido");

    // 开发安装脚本也编译此源码，避免两套安全规则逐渐分叉。
    internal static string ValidateIsolatedRoot(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\?\", StringComparison.Ordinal)
            || value.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{EnvironmentVariable} 必须是普通绝对路径。", nameof(value));
        }

        var relative = value[Path.GetPathRoot(value)!.Length..];
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if ((segment is not "." and not ".." && (segment.EndsWith(' ') || segment.EndsWith('.')))
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException($"{EnvironmentVariable} 包含无效的目录名。", nameof(value));
            }
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var forbidden in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AniMeido"),
            Path.Combine(local, "AniMeido"),
            Path.Combine(local, "Programs", "AniMeido"),
        })
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(forbidden));
            if (root.Equals(path, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{EnvironmentVariable} 不能使用真实数据或安装目录：{path}", nameof(value));
            }
        }

        // 不先用 Exists：它会把访问失败当成不存在，无法保证校验失败时关闭入口。
        var ancestors = new Stack<string>();
        for (string? path = root; path is not null; path = Path.GetDirectoryName(path))
            ancestors.Push(path);
        // 从外到内检查，避免在发现 junction 前已经沿它访问下级路径。
        while (ancestors.TryPop(out var path))
            CheckDirectory(path);
        foreach (var directory in new[] { "Roaming", "Local", "Pictures" })
            CheckDirectory(Path.Combine(root, directory));

        Directory.CreateDirectory(root);
        return root;
    }

    private static void CheckDirectory(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"{EnvironmentVariable} 路径中不能有 junction 或符号链接：{path}");
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException($"{EnvironmentVariable} 路径已是文件，不能作为数据目录：{path}");
    }
}
