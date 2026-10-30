using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AniMeido.Logging;

/// <summary>App 与 Host 共用文件轮转和保留规则；消息格式及根目录仍由各自适配器提供。</summary>
internal sealed class DiagnosticFileStore(string warningFileStem)
{
    private const long MaxWarningFileBytes = 50 * 1024;
    private const int MaxWarningEventBytes = 8 * 1024;
    private readonly object _gate = new();
    private DateOnly _lastCleanupDay;
    // 记住当天当前文件，避免每条 Warning 都重新打开已写满的文件。
    private (DateOnly Day, int Index) _warningFile;

    public void Write(string root, bool isWarning, DateTimeOffset localTime, string kind, string text)
    {
        lock (_gate)
        {
            var day = DateOnly.FromDateTime(localTime.DateTime);
            if (_lastCleanupDay != day)
                CleanupOldDays(root, day);

            var directory = Path.Combine(root, isWarning ? "warning" : "error", DayName(day));
            Directory.CreateDirectory(directory);
            if (isWarning)
                WriteWarning(directory, day, text);
            else
                WriteError(directory, localTime, kind, text);
        }
    }

    public void PruneOldDays(string root, DateOnly today)
    {
        lock (_gate)
            CleanupOldDays(root, today);
    }

    private void WriteWarning(string directory, DateOnly day, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxWarningEventBytes)
            bytes = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(bytes, 0, MaxWarningEventBytes - 128)
                + "\n[Warning truncated]\n");

        for (var index = _warningFile.Day == day ? _warningFile.Index : 0; ; index++)
        {
            var name = index == 0 ? $"{warningFileStem}.log" : $"{warningFileStem}.{index}.log";
            var path = Path.Combine(directory, name);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            if (stream.Length + bytes.Length > MaxWarningFileBytes && stream.Length > 0)
                continue;

            stream.Write(bytes);
            _warningFile = (day, index);
            return;
        }
    }

    private static void WriteError(string directory, DateTimeOffset localTime, string kind, string text)
    {
        kind = new string(kind.Where(char.IsLetterOrDigit).Take(64).ToArray());
        if (kind.Length == 0)
            kind = "Error";
        var timestamp = localTime.ToString("HH-mm-ss-fff", CultureInfo.InvariantCulture);
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var index = 1; ; index++)
        {
            var path = Path.Combine(directory, $"{timestamp}-{kind}-{index:000}.log");
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException) when (File.Exists(path))
            {
                // 同一毫秒的其他错误已经使用了这个文件名。
                continue;
            }

            using (stream)
            {
                stream.Write(bytes);
            }
            return;
        }
    }

    private void CleanupOldDays(string root, DateOnly today)
    {
        _lastCleanupDay = today;
        foreach (var level in new[] { "warning", "error" })
        {
            var directory = Path.Combine(root, level);
            try
            {
                if (!Directory.Exists(directory))
                    continue;

                foreach (var path in Directory.EnumerateDirectories(directory))
                {
                    if (!DateOnly.TryParseExact(Path.GetFileName(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var day)
                        || day >= today.AddDays(-2))
                        continue;

                    try
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                            Directory.Delete(path, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Debug.WriteLine($"[DiagnosticLog] Cannot prune {path}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[DiagnosticLog] Cannot prune logs: {ex.Message}");
            }
        }
    }

    private static string DayName(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
