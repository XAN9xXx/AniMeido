using AniMeido.Contracts;

namespace AniMeido.Tests;

/// <summary>
/// 测试用的 IAppDataPaths 实现，使用临时目录避免污染真实 AppData。
/// </summary>
public sealed class MockAppDataPaths : IAppDataPaths, IDisposable
{
    private static readonly object CleanupLogGate = new();
    private bool _disposed;

    /// <summary>本实例自行创建的根目录；不包含调用方传入的数据库所在目录。</summary>
    public string RootDirectory { get; }

    public MockAppDataPaths(string? dbPath = null)
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), $"AniMeidoTest_AppData_{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootDirectory);
        if (dbPath != null)
        {
            DatabasePath = dbPath;
        }
        else
        {
            DatabasePath = Path.Combine(RootDirectory, "AniMeido.db");
        }
        BackupDirectory = Path.Combine(RootDirectory, "Backups");
        LogDirectory = Path.Combine(RootDirectory, "logs");
    }

    public string DatabasePath { get; }
    public string BackupDirectory { get; }
    public string LogDirectory { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 工厂使用 ReadWriteCreate + Shared；测试直接 SQL 使用默认连接串，分别清此库的池。
        using var factoryConnection = new AniMeido.Plugin.Base.Services.SqliteConnectionFactory(this).CreateConnection();
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(factoryConnection);
        using var testConnection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(testConnection);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // DatabaseService.InitializeAsync 的目标备份也使用默认连接池；只清本实例根目录中的库。
                if (Directory.Exists(RootDirectory))
                {
                    foreach (var database in Directory.EnumerateFiles(RootDirectory, "*.db", SearchOption.AllDirectories))
                    {
                        using var backupConnection = new Microsoft.Data.Sqlite.SqliteConnection(
                            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database }.ToString());
                        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(backupConnection);
                    }
                }
                if (Directory.Exists(RootDirectory)) Directory.Delete(RootDirectory, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt < 10)
                {
                    Thread.Sleep(100);
                    continue;
                }

                var line = $"{DateTimeOffset.UtcNow:O}\t{RootDirectory}\t{exception.GetType().Name}\t{exception.Message.ReplaceLineEndings(" ")}{Environment.NewLine}";
                try
                {
                    lock (CleanupLogGate)
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "AniMeidoTest_cleanup-failures.log"), line);
                }
                catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"{line}Cleanup log unavailable: {logException.Message}");
                }
                return;
            }
        }
    }
}
