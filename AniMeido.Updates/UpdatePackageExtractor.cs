using System.IO.Compression;

namespace AniMeido.Updates;

public static class UpdatePackageExtractor
{
    public static void Extract(string zipPath, string targetDirectory, IReadOnlyList<string> requiredRootFiles,
        UpdateExtractionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(requiredRootFiles);
        limits ??= new UpdateExtractionLimits();
        if (limits.MaxEntries < 1 || limits.MaxFileBytes < 1 || limits.MaxTotalBytes < 1)
        {
            throw new UpdateException(UpdateErrorKind.ArchiveLimitExceeded, "解压限制必须为正数。");
        }

        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(parent))
        {
            throw new UpdateException(UpdateErrorKind.InvalidArchive, "不能把文件系统根目录作为更新目标。");
        }

        var staging = target + ".staging";
        var stagingPrefix = staging + Path.DirectorySeparatorChar;
        var zip = Path.GetFullPath(zipPath);
        // 在删除残留目录之前核对绝对路径：staging 必须是目标的并列目录，且不能包含输入包。
        var parentPrefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(staging).StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase)
            || zip.Equals(staging, StringComparison.OrdinalIgnoreCase)
            || zip.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdateErrorKind.InvalidArchive, "更新暂存目录与输入包路径冲突。");
        }

        foreach (var required in requiredRootFiles)
        {
            ValidateName(required);
            if (required.Contains('/') || required.EndsWith('/'))
            {
                throw new UpdateException(UpdateErrorKind.InvalidArchive, "必需文件必须是根级文件名。");
            }
        }

        try
        {
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new UpdateException(UpdateErrorKind.TargetExists, "更新目标已存在，不能覆盖。");
            }

            DeleteStaging(staging);
            Directory.CreateDirectory(staging);
            try
            {
                ExtractCore(zip, staging, limits);
                foreach (var required in requiredRootFiles)
                {
                    if (!File.Exists(Path.Combine(staging, required)))
                    {
                        throw new UpdateException(UpdateErrorKind.RequiredFileMissing, $"更新包缺少必需的根级文件：{required}。");
                    }
                }

                Directory.Move(staging, target);
            }
            catch (Exception original)
            {
                try
                {
                    DeleteStaging(staging);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    throw new UpdateException(UpdateErrorKind.FileSystem, "解压失败，且无法清理暂存目录。",
                        new AggregateException(original, cleanup));
                }

                throw;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException(UpdateErrorKind.InvalidArchive, "更新包不是有效的 ZIP 文件。", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateErrorKind.FileSystem, "更新包解压或目录切换失败。", ex);
        }
    }

    private static void ExtractCore(string zip, string staging, UpdateExtractionLimits limits)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > limits.MaxEntries)
        {
            throw new UpdateException(UpdateErrorKind.ArchiveLimitExceeded, "更新包条目数超过限制。");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = staging + Path.DirectorySeparatorChar;
        long total = 0;
        var buffer = new byte[81920];
        foreach (var entry in archive.Entries)
        {
            var relative = ValidateName(entry.FullName);
            if (!paths.Add(relative) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new UpdateException(UpdateErrorKind.InvalidArchive, "更新包包含重复路径或符号链接。");
            }

            var destination = Path.GetFullPath(Path.Combine(staging, relative));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(UpdateErrorKind.InvalidArchive, "更新包路径超出暂存目录。");
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long fileBytes = 0;
            var crc = uint.MaxValue;
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                // 只按实际读取的数据累计；ZIP 头中的 Length 不能作为安全边界。
                if (count > limits.MaxFileBytes - fileBytes || count > limits.MaxTotalBytes - total)
                {
                    throw new UpdateException(UpdateErrorKind.ArchiveLimitExceeded, "更新包实际解压量超过限制。");
                }

                output.Write(buffer, 0, count);
                for (var i = 0; i < count; i++)
                {
                    crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
                }

                fileBytes += count;
                total += count;
            }

            // .NET 8 的 ZIP 流可能按虚报的较小长度截断输出；CRC 能识别这类损坏。
            // Length 只在结束时做完整性比较，绝不用于替代实际写入量限制。
            if (fileBytes != entry.Length || ~crc != entry.Crc32)
            {
                throw new UpdateException(UpdateErrorKind.InvalidArchive, "更新包条目的实际长度或 CRC32 不一致。");
            }
        }
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '/' || name.Contains(':') || name.Contains('\\')
            || name.Contains('\0') || Path.IsPathRooted(name))
        {
            throw new UpdateException(UpdateErrorKind.InvalidArchive, "更新包包含非法条目名。");
        }

        // ZIP 的目录条目可以带一个结尾斜杠；除此以外空路径段一律非法。
        var relative = name.EndsWith('/') ? name[..^1] : name;
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(c => c < 32 || c is '<' or '>' or '"' or '|' or '?' or '*')
                || IsDeviceName(segment))
            {
                throw new UpdateException(UpdateErrorKind.InvalidArchive, "更新包路径段不适用于 Windows。");
            }
        }

        return relative;
    }

    private static bool IsDeviceName(string segment)
    {
        var stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && stem[3] is >= '1' and <= '9'
                && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)));
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320U ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    private static void DeleteStaging(string staging)
    {
        if (Directory.Exists(staging))
        {
            // 不递归进入被替换成链接的暂存根目录。
            var reparse = (File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0;
            Directory.Delete(staging, !reparse);
        }
    }
}
