using AniMeido.Contracts;

namespace AniMeido.App.Services;

/// <summary>
/// 应用数据路径的 App 层实现。
/// </summary>
public sealed class AppDataPaths : IAppDataPaths
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AniMeido");

    public string DatabasePath => Path.Combine(AppDataDir, "AniMeido.db");
    public string BackupDirectory => Path.Combine(AppDataDir, "Backups");
    public string LogDirectory => Path.Combine(AppDataDir, "logs");
}
