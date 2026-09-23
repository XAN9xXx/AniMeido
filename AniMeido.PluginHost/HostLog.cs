using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AniMeido.PluginHost;

internal static class HostLog
{
    private const long MaxWarningFileBytes = 50 * 1024;
    private const int MaxWarningEventBytes = 8 * 1024;
    private static readonly object Gate = new();
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AniMeido", "logs");
    private static DateOnly _lastCleanupDay;
    // 当天正在写的 warnings 文件序号，避免每条都从第一个文件逐个打开查找。
    private static (DateOnly Day, int Index) _warningFile;

    public static void Warning(string message, Exception? exception = null)
        => Write("Warning", message, exception);

    public static void Error(string message, Exception? exception = null)
        => Write("Error", message, exception);

    public static void Fatal(string message, Exception? exception = null)
        => Write("Fatal", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                var now = DateTimeOffset.Now;
                var day = DateOnly.FromDateTime(now.DateTime);
                if (_lastCleanupDay != day)
                    CleanupOldDays(day);

                var text = $"{now:O} [{level}] [PluginHost] {message}{Environment.NewLine}"
                    + (exception is null ? "" : exception + Environment.NewLine);
                var directory = Path.Combine(Root, level == "Warning" ? "warning" : "error",
                    day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                if (level == "Warning")
                    WriteWarning(directory, day, text);
                else
                    WriteError(directory, now, exception?.GetType().Name ?? level, text);
            }
        }
#pragma warning disable CA1031 // 日志写入不能影响 PluginHost 退出流程。
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginHost] Cannot write log: {ex}");
        }
#pragma warning restore CA1031
    }

    private static void WriteWarning(string directory, DateOnly day, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxWarningEventBytes)
            bytes = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(bytes, 0, MaxWarningEventBytes - 128)
                + "\n[Warning truncated]\n");

        for (var index = _warningFile.Day == day ? _warningFile.Index : 0; ; index++)
        {
            var name = index == 0
                ? "plugin-host-warnings.log"
                : $"plugin-host-warnings.{index}.log";
            var path = Path.Combine(directory, name);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            if (stream.Length + bytes.Length > MaxWarningFileBytes && stream.Length > 0)
                continue;

            stream.Write(bytes);
            _warningFile = (day, index);
            return;
        }
    }

    private static void WriteError(
        string directory, DateTimeOffset now, string kind, string text)
    {
        var timestamp = now.ToString("HH-mm-ss-fff", CultureInfo.InvariantCulture);
        kind = new string(kind.Where(char.IsLetterOrDigit).Take(64).ToArray());
        if (kind.Length == 0)
            kind = "Error";
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var index = 1; ; index++)
        {
            var path = Path.Combine(directory, $"{timestamp}-{kind}-{index:000}.log");
            try
            {
                using var stream = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                return;
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
    }

    /// <summary>
    /// 删除两天前的日志目录。每天只尝试一次；删不掉的目录（文件被占用、只读，
    /// 或主程序正在删同一目录）跳过，不影响写入，也不会让之后每条日志都重试。
    /// </summary>
    private static void CleanupOldDays(DateOnly today)
    {
        _lastCleanupDay = today;
        foreach (var level in new[] { "warning", "error" })
        {
            var directory = Path.Combine(Root, level);
            try
            {
                if (!Directory.Exists(directory))
                    continue;

                foreach (var path in Directory.EnumerateDirectories(directory))
                {
                    if (!DateOnly.TryParseExact(
                            Path.GetFileName(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var day)
                        || day >= today.AddDays(-2))
                    {
                        continue;
                    }

                    try
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                            Directory.Delete(path, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Debug.WriteLine($"[PluginHost] Cannot prune {path}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[PluginHost] Cannot prune logs: {ex.Message}");
            }
        }
    }
}
