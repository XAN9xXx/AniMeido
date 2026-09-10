using AniMeido.Contracts;

namespace AniMeido.Tests;

/// <summary>
/// 测试用的 IAppDataPaths 实现，使用临时目录避免污染真实 AppData。
/// </summary>
public sealed class MockAppDataPaths : IAppDataPaths
{
    private readonly string _baseDir;

    public MockAppDataPaths(string? dbPath = null)
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"AniMeidoTest_AppData_{Guid.NewGuid():N}");
        if (dbPath != null)
        {
            DatabasePath = dbPath;
        }
        else
        {
            Directory.CreateDirectory(_baseDir);
            DatabasePath = Path.Combine(_baseDir, "AniMeido.db");
        }
        BackupDirectory = Path.Combine(_baseDir, "Backups");
        LogDirectory = Path.Combine(_baseDir, "logs");
    }

    public string DatabasePath { get; }
    public string BackupDirectory { get; }
    public string LogDirectory { get; }
}
