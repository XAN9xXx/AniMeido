using AniMeido.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace AniMeido.App.Services;

/// <summary>适配 Serilog 消息；文件轮转和保留规则与 Host 共用。</summary>
internal sealed class DiagnosticFileSink : ILogEventSink
{
    private readonly string _root;
    private readonly DiagnosticFileStore _files = new("warnings");

    public DiagnosticFileSink(string root)
    {
        _root = root;
        try
        {
            _files.PruneOldDays(root, DateOnly.FromDateTime(DateTime.Now));
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
            var localTime = logEvent.Timestamp.ToLocalTime();
            var kind = logEvent.Exception?.GetType().Name
                ?? (logEvent.Level == LogEventLevel.Fatal ? "Fatal" : "Error");
            _files.Write(_root, logEvent.Level == LogEventLevel.Warning, localTime, kind, Format(logEvent, localTime));
        }
#pragma warning disable CA1031 // 日志写入不能让业务操作失败，也不能递归写入日志。
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiagnosticLog] Cannot write log: {ex}");
        }
#pragma warning restore CA1031
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
}
