using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AniMeido.Updates.Install;

namespace AniMeido.Launcher;

internal static partial class Program
{
    private const uint YesNoWarning = 0x00000004 | 0x00000030;
    private const uint ErrorMessage = 0x00000010;
    internal static string DataDirectory => AppDataRoots.Roaming;

    [STAThread]
    private static int Main(string[] args)
    {
        LauncherLog? log = null;
        try
        {
            var dataDirectory = DataDirectory;
            log = new LauncherLog(Path.Combine(dataDirectory, "logs", "launcher.log"));
            if (AppDataRoots.IsIsolated)
                log.Write($"[Warning] 独立数据模式，数据根目录：{AppDataRoots.IsolatedRoot}");
            var executable = Environment.ProcessPath ?? throw new InstallException("无法确定启动器位置。");
            var root = Path.GetDirectoryName(executable)!;
            var forwarded = new List<string>();
            int? waitPid = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--wait-pid")
                {
                    if (waitPid is not null || ++i >= args.Length || !int.TryParse(args[i], out var pid) || pid <= 0)
                    {
                        throw new InstallException("--wait-pid 必须且只能指定一个有效的进程 ID。");
                    }
                    waitPid = pid;
                }
                else
                {
                    forwarded.Add(args[i]);
                }
            }
            if (waitPid is { } processId)
            {
                WaitForExit(processId);
            }

            var manager = new InstallManager(new InstallStateStore(root, log: log.Write), log.Write);
            manager.Launch(() => HasRunningApp(root), app => StartApp(app, forwarded, log), ChooseRollback, dataDirectory);
            return 0;
        }
#pragma warning disable CA1031 // 启动器最外层必须兜底提示，不能让 WinExe 静默退出。
        catch (Exception ex)
        {
            log?.Write($"致命错误：{ex}");
            var logHint = log is null ? "" : $"\n\n日志：{log.Path}";
            MessageBoxW(0, $"{ex.Message}{logHint}", "AniMeido 无法启动", ErrorMessage);
            return 1;
        }
#pragma warning restore CA1031
    }

    private static void StartApp(string app, IEnumerable<string> forwarded, LauncherLog log)
    {
        var start = new ProcessStartInfo(app)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(app)!,
        };
        foreach (var argument in forwarded)
        {
            start.ArgumentList.Add(argument);
        }
        using var started = Process.Start(start) ?? throw new InstallException("无法创建 AniMeido.App.exe 进程。");
        log.Write($"已启动 {app}（PID {started.Id}）。");
    }

    private static RollbackChoice ChooseRollback(RollbackRequest request)
    {
        var message = $"新版本 {request.Version} 连续两次未能正常启动。\n数据库已为新版本升级（或正在升级）。\n\n"
            + $"选“是”：恢复上一版本 {request.Previous}，数据回到更新前。"
            + $"新版本期间的数据将另存到：\n{request.ArchiveDirectory}\n\n"
            + "选“否”：保留新版本，再试一次。";
        return MessageBoxW(0, message, "AniMeido 更新后无法启动", YesNoWarning) == 6
            ? RollbackChoice.Restore : RollbackChoice.Keep;
    }

    private static void WaitForExit(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            throw new InstallException("启动器不能等待自己退出。");
        }
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return; // 进程已退出。
        }
        using (process)
        {
            if (!process.WaitForExit(30_000))
            {
                throw new InstallException("等待主程序退出超过 30 秒；未切换版本，请关闭主程序后重试。");
            }
        }
    }

    private static bool HasRunningApp(string root)
    {
        var processes = Process.GetProcessesByName("AniMeido.App");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null)
                    {
                        if (process.HasExited)
                        {
                            continue;
                        }
                        throw new InstallException("无法确认现有 AniMeido.App.exe 的路径，拒绝切换或恢复数据。");
                    }
                    var versionDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (versionDirectory is not null
                        && InstallLayout.TryGetDirectoryVersion(Path.GetFileName(versionDirectory), out _)
                        && string.Equals(Path.GetDirectoryName(versionDirectory), root, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // 枚举后进程已经结束。
                }
                catch (Win32Exception ex)
                {
                    if (!process.HasExited)
                    {
                        throw new InstallException("无法读取现有 AniMeido.App.exe 的主模块路径，未切换版本。", ex);
                    }
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint window, string text, string caption, uint type);
}
