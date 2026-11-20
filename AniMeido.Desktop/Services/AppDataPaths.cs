using AniMeido.Contracts;

namespace AniMeido.App.Services;

public sealed class AppDataPaths : IAppDataPaths
{
    private static string AppDataDir => AppDataRoots.Roaming;

    public string DatabasePath => Path.Combine(AppDataDir, "AniMeido.db");
    public string BackupDirectory => Path.Combine(AppDataDir, "Backups");
    public string LogDirectory => Path.Combine(AppDataDir, "logs");
}
