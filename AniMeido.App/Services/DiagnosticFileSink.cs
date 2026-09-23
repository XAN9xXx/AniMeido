using System.Diagnostics;
using System.Globalization;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace AniMeido.App.Services;

/// <summary>按严重程度保存诊断日志，不让日志写入失败影响应用。</summary>
internal sealed class DiagnosticFileSink : ILogEventSink
{
    private const long MaxWarningFileBytes = 50 * 1024;
    private const int MaxWarningEventBytes = 8 * 1024;
    private readonly object _gate = new();
    private readonly string _root;
    private DateOnly _lastCleanupDay;

    public DiagnosticFileSink(string root)
    {
        _root = root;
        try
        {
            CleanupOldDays(DateOnly.FromDateTime(DateTime.Now));
        }
#pragma warning disable CA1031 // 日志清理不能阻止应用启动。
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiagnosticLog] Cannot prune logs: {ex}");
        }
#pragma warning restore CA1031
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning)
            return;

        try
        {
            lock (_gate)
            {
                var localTime = logEvent.Timestamp.ToLocalTime();
                var today = DateOnly.FromDateTime(localTime.DateTime);
                if (_lastCleanupDay != today)
                    CleanupOldDays(today);

                var text = Format(logEvent, localTime);
                if (logEvent.Level == LogEventLevel.Warning)
                    WriteWarning(today, text);
                else
                    WriteError(today, localTime, logEvent, text);
            }
        }
#pragma warning disable CA1031 // 日志写入不能让业务操作失败，也不能递归写入日志。
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiagnosticLog] Cannot write log: {ex}");
        }
#pragma warning restore CA1031
    }

    private void WriteWarning(DateOnly day, string text)
    {
        var directory = Path.Combine(_root, "warning", DayName(day));
        Directory.CreateDirectory(directory);
        // 单条异常文本也不能突破 Warning 的单文件上限。
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxWarningEventBytes)
            bytes = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(bytes, 0, MaxWarningEventBytes - 128)
                + "\n[Warning truncated]\n");
        for (var index = 0; ; index++)
        {
            var name = index == 0 ? "warnings.log" : $"warnings.{index}.log";
            var path = Path.Combine(directory, name);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            if (stream.Length + bytes.Length > MaxWarningFileBytes && stream.Length > 0)
                continue;

            stream.Write(bytes);
            return;
        }
    }

    private void WriteError(
        DateOnly day,
        DateTimeOffset localTime,
        LogEvent logEvent,
        string text)
    {
        var directory = Path.Combine(_root, "error", DayName(day));
        Directory.CreateDirectory(directory);
        var kind = SafeKind(logEvent.Exception?.GetType().Name
            ?? (logEvent.Level == LogEventLevel.Fatal ? "Fatal" : "Error"));
        var timestamp = localTime.ToString("HH-mm-ss-fff", CultureInfo.InvariantCulture);
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
                // 同一毫秒内的另一条错误已使用该文件名。
            }
        }
    }

    private static string Format(LogEvent logEvent, DateTimeOffset localTime)
    {
        var source = logEvent.Properties.TryGetValue("SourceContext", out var value)
            && value is ScalarValue { Value: string context }
                ? context
                : "Application";
        var result = new StringBuilder()
            .Append(localTime.ToString("O", CultureInfo.InvariantCulture))
            .Append(" [").Append(logEvent.Level).Append("] ")
            .Append('[').Append(source).Append("] ")
            .AppendLine(logEvent.RenderMessage());
        if (logEvent.Exception is not null)
            result.AppendLine(logEvent.Exception.ToString());
        return result.ToString();
    }

    private void CleanupOldDays(DateOnly today)
    {
        foreach (var level in new[] { "warning", "error" })
        {
            var directory = Path.Combine(_root, level);
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(path);
                if (DateOnly.TryParseExact(
                        name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day)
                    && day < today.AddDays(-2)
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(path, recursive: true);
            }
        }
        _lastCleanupDay = today;
    }

    private static string DayName(DateOnly day)
        => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string SafeKind(string value)
    {
        var name = new string(value.Where(char.IsLetterOrDigit).Take(64).ToArray());
        return name.Length > 0 ? name : "Error";
    }
}
