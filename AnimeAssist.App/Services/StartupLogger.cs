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
        System.IO.Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.File(
                System.IO.Path.Combine(logDir, "aniMeido.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 3)
            .CreateLogger();
    }
}
