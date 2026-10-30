using AniMeido.Logging;
using System.Diagnostics;

namespace AniMeido.PluginHost;

internal static class HostLog
{
    private static readonly DiagnosticFileStore Files = new($"plugin-host-{Environment.ProcessId}-warnings");
    private static string Root => Path.Combine(AppDataRoots.Roaming, "logs");

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
            var now = DateTimeOffset.Now;
            var text = $"{now:O} [{level}] [PluginHost] {message}{Environment.NewLine}"
                + (exception is null ? "" : exception + Environment.NewLine);
            Files.Write(Root, level == "Warning", now, exception?.GetType().Name ?? level, text);
        }
#pragma warning disable CA1031 // 日志写入不能影响 PluginHost 退出流程。
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginHost] Cannot write log: {ex}");
        }
#pragma warning restore CA1031
    }
}
