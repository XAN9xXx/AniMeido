using Serilog;

namespace AniMeido.App.Services;

/// <summary>
/// Serilog 日志初始化。在 App 构造时、读取数据之前调用。
/// </summary>
internal static class StartupLogger
{
    internal static string LogDirectory => Path.Combine(AppDataRoots.Roaming, "logs");

    public static void Initialize()
    {
        var logDir = LogDirectory;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(new DiagnosticFileSink(logDir))
            .CreateLogger();

        if (AppDataRoots.IsIsolated)
            Log.Warning("独立数据模式，数据根目录：{DataRoot}", AppDataRoots.IsolatedRoot);
    }
}
