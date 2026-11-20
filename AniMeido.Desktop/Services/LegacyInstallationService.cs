using System.Diagnostics;
using Microsoft.Win32;
using Serilog;

namespace AniMeido.App.Services;

internal sealed record LegacyInstallation(RegistryHive Hive, string InstallLocation,
    string Uninstaller, string Arguments);

/// <summary>只在安装模式检测旧登记；检测失败时不显示不完整的迁移提示。</summary>
internal sealed class LegacyInstallationService
{
    private readonly InstalledAppContext _context;
    private readonly IInstallationRegistry _registry;
    private readonly Func<ProcessStartInfo, Task<bool>> _runUninstaller;

    public LegacyInstallationService(InstalledAppContext context, IInstallationRegistry registry)
        : this(context, registry, RunUninstallerAsync)
    {
    }

    internal LegacyInstallationService(InstalledAppContext context, IInstallationRegistry registry,
        Func<ProcessStartInfo, Task<bool>> runUninstaller)
    {
        _context = context;
        _registry = registry;
        _runUninstaller = runUninstaller;
    }

    internal IReadOnlyList<LegacyInstallation> Detect()
    {
        if (!_context.IsInstalled)
            return [];
        try
        {
            var installations = new List<LegacyInstallation>();
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                if (_registry.ReadLegacyInstallation(hive) is not { } entry)
                    continue;
                var (file, arguments) = SplitUninstallString(entry.UninstallString);
                installations.Add(new LegacyInstallation(hive, entry.InstallLocation, file, arguments));
            }
            return installations;
        }
#pragma warning disable CA1031 // 注册表读取/格式失败不得阻断启动，也不显示部分检测结果。
        catch (Exception ex)
        {
            Log.Warning(ex, "无法检测旧版 AniMeido 安装，已跳过迁移提示");
            return [];
        }
#pragma warning restore CA1031
    }

    internal async Task<bool> UninstallAsync(LegacyInstallation installation)
    {
        if (!_context.IsInstalled)
            return false;
        try
        {
            var success = await _runUninstaller(new ProcessStartInfo(installation.Uninstaller)
            {
                Arguments = installation.Arguments,
                UseShellExecute = true,
                Verb = "runas",
            });
            if (!success)
                Log.Warning("旧版 AniMeido 卸载未完成，请在 Windows 已安装的应用中手动卸载");
            return success;
        }
#pragma warning disable CA1031 // 拒绝 UAC、取消或启动失败均不影响新安装的使用。
        catch (Exception ex)
        {
            Log.Warning(ex, "无法运行旧版 AniMeido 卸载程序，请稍后手动卸载");
            return false;
        }
#pragma warning restore CA1031
    }

    internal static (string File, string Arguments) SplitUninstallString(string command)
    {
        command = command.Trim();
        string file;
        string arguments;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0)
                throw new FormatException("旧版卸载命令的引号不完整。");
            file = command[1..end];
            arguments = command[(end + 1)..].Trim();
        }
        else
        {
            // Inno 的命令通常带引号；兼容未带引号且路径中含空格的旧登记。
            var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                throw new FormatException("旧版卸载命令不包含 exe。");
            file = command[..(end + 4)];
            arguments = command[(end + 4)..].Trim();
        }
        if (!Path.IsPathFullyQualified(file) || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("旧版卸载程序必须是绝对 exe 路径。");
        return (file, arguments);
    }

    private static async Task<bool> RunUninstallerAsync(ProcessStartInfo start)
    {
        var shortcuts = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AniMeido.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AniMeido.lnk"),
        };
        return await PreserveShortcutsAsync(shortcuts, async () =>
        {
            using var process = Process.Start(start);
            if (process is null)
                return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        });
    }

    internal static async Task<bool> PreserveShortcutsAsync(IEnumerable<string> shortcuts, Func<Task<bool>> uninstall)
    {
        // 旧 HKCU 卸载日志可能删除新包创建的同名链接。仅暂存在内存中，保留 AppUserModelID。
        var saved = new List<(string Path, byte[] Bytes)>();
        foreach (var path in shortcuts)
        {
            try
            {
                if (File.Exists(path))
                    saved.Add((path, File.ReadAllBytes(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "无法暂存新版本快捷方式：{Path}", path);
            }
        }
        try
        {
            return await uninstall();
        }
        finally
        {
            foreach (var (path, bytes) in saved)
            {
                try
                {
                    // 链接仍在时不写入，避免覆盖用户在等待卸载时修改的内容。
                    if (!File.Exists(path))
                        File.WriteAllBytes(path, bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "无法恢复新版本快捷方式：{Path}", path);
                }
            }
        }
    }
}
