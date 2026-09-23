using Serilog;

namespace AniMeido.App.Services;

/// <summary>
/// Serilog 日志初始化。在 App.OnLaunched 最初调用。
/// </summary>
internal static class StartupLogger
{
    public static void Initialize()
    {
        var logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AniMeido", "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(new DiagnosticFileSink(logDir))
            .CreateLogger();
    }
}
