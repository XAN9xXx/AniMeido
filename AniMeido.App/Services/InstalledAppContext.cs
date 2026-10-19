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

    internal string ValidateRestore(string dataDirectory, string directory) => RequireManager()
        .ValidateRestore(dataDirectory, directory, AniMeido.Plugin.Base.Services.DatabaseSchema.CurrentVersion);

    internal void RequestRestore(string dataDirectory, string directory) => RequireManager()
        .RequestRestore(dataDirectory, directory, AniMeido.Plugin.Base.Services.DatabaseSchema.CurrentVersion);

    internal void ShowRestoreNotice(Action<RestoreNotice> show)
    {
        if (_manager?.ReadRestoreNotice() is not { } notice)
            return;
        show(notice);
        // 仅在提示成功显示之后清除；若其它进程写入新结果，不清除新的结果。
        _manager.ClearRestoreNotice(notice);
    }

    internal void ShowUnprocessedRestoreRequest(Action<PendingRestoreRequest> show)
    {
        if (_manager?.ReadPendingRestore() is not { } request)
            return;
        show(request);
        // 提示后撤销请求，用户可在更新启动器后重新发起；若期间写入了新请求则不清除。
        _manager.ClearPendingRestore(request);
    }

    private InstallManager RequireManager() => _manager
        ?? throw new InvalidOperationException("只有安装模式支持恢复数据。");

    public IReadOnlyList<CleanupError> CleanupObsolete() => _manager?.CleanupObsolete() ?? [];

    internal int SetNotificationIdentity()
    {
        if (!IsInstalled)
        {
            return 0;
        }
        // 安装器创建的快捷方式必须使用同一个 AppUserModelID，保持跨版本通知身份一致。
        return SetCurrentProcessExplicitAppUserModelID("AniMeido.Desktop");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
