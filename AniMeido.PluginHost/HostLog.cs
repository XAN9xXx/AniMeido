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
                    WriteWarning(directory, text);
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

    private static void WriteWarning(string directory, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxWarningEventBytes)
            bytes = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(bytes, 0, MaxWarningEventBytes - 128)
                + "\n[Warning truncated]\n");

        for (var index = 0; ; index++)
        {
            var name = index == 0
                ? "plugin-host-warnings.log"
                : $"plugin-host-warnings.{index}.log";
            var path = Path.Combine(directory, name);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            if (stream.Length + bytes.Length > MaxWarningFileBytes && stream.Length > 0)
                continue;

            stream.Write(bytes);
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

    private static void CleanupOldDays(DateOnly today)
    {
        foreach (var level in new[] { "warning", "error" })
        {
            var directory = Path.Combine(Root, level);
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateDirectories(directory))
            {
                if (DateOnly.TryParseExact(
                        Path.GetFileName(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day)
                    && day < today.AddDays(-2)
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(path, recursive: true);
            }
        }
        _lastCleanupDay = today;
    }
}
