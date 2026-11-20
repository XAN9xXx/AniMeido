using Serilog;

namespace AniMeido.App.Services;

internal sealed class InstallationRegistrationService(
    InstalledAppContext context, IInstallationRegistry registry)
{
    // App 只在 MarkHealthy 成功返回后调用；登记失败不改变健康标记。
    internal void UpdateDisplayVersion()
    {
        if (!context.IsInstalled)
            return;
        try
        {
            registry.UpdateDisplayVersionIfPresent(context.Version!);
        }
#pragma warning disable CA1031 // 卸载登记不可写不能影响已经健康启动的程序。
        catch (Exception ex)
        {
            Log.Warning(ex, "无法更新 AniMeido 卸载登记中的 DisplayVersion");
        }
#pragma warning restore CA1031
    }
}
