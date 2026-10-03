using System.Runtime.InteropServices;
using AniMeido.Updates.Install;

namespace AniMeido.App.Services;

/// <summary>只有并列版本目录 + state.json 才启用；开发运行及旧安装完全保持原行为。</summary>
public sealed class InstalledAppContext
{
    private readonly InstallManager? _manager;

    public bool IsInstalled => _manager is not null;
    public string? Version { get; }
    public string? InstallRoot { get; }
    internal Task StartupCleanup { get; set; } = Task.CompletedTask;

    public InstalledAppContext() : this(Environment.ProcessPath)
    {
    }

    public InstalledAppContext(string? executablePath, TimeSpan? lockTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (directory is null || !InstallLayout.TryGetDirectoryVersion(Path.GetFileName(directory), out var version))
        {
            return;
        }
        var root = Path.GetDirectoryName(directory);
        if (root is null || !File.Exists(Path.Combine(root, "state.json")))
        {
            return;
        }
        InstallRoot = root;
        Version = version;
        _manager = new InstallManager(new InstallStateStore(root, lockTimeout,
            message => Serilog.Log.Warning("安装状态：{Message}", message)));
    }

    public void MarkHealthy() => _manager?.MarkHealthy(Version!);

    public void RecordMigration(string backupPath) => _manager?.RecordMigration(Version!, backupPath);

    public RolledBackNotice? ConsumeRolledBackNotice() => _manager?.ConsumeRolledBackNotice();

    public IReadOnlyList<CleanupError> CleanupObsolete() => _manager?.CleanupObsolete() ?? [];

    internal int SetNotificationIdentity()
    {
        if (!IsInstalled)
        {
            return 0;
        }
        // U4 的快捷方式必须设置同一个 AppUserModelID；通知设置跨版本保留仍需人工验收。
        return SetCurrentProcessExplicitAppUserModelID("AniMeido.Desktop");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
