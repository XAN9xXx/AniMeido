namespace AniMeido.App.Services;

internal abstract record AppUpdateCheckResult(string? LatestVersion)
{
    internal sealed record Installed(InstalledUpdateResult Result)
        : AppUpdateCheckResult(Result.LatestVersion);

    internal sealed record Legacy(UpdateService.UpdateCheckResult Result)
        : AppUpdateCheckResult(Result.LatestVersion);
}

/// <summary>设置页更新检查的完整入口；安装状态和通道选择不由页面编排。</summary>
public sealed class AppUpdateCheckService(
    InstalledAppContext context,
    DesktopSettingsStore settings,
    InstalledUpdateService installedUpdates,
    UpdateService legacyUpdates)
{
    internal Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        // 设置和安装状态读取可能同步访问磁盘，整次检查从后台开始。
        => Task.Run(() => CheckCoreAsync(cancellationToken), cancellationToken);

    private async Task<AppUpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        if (!context.IsInstalled)
        {
            var result = await legacyUpdates.CheckForUpdateAsync(cancellationToken).ConfigureAwait(false);
            return new AppUpdateCheckResult.Legacy(result);
        }

        var currentSettings = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var installedResult = await installedUpdates.CheckAsync(
            currentSettings.UpdateChannel, cancellationToken).ConfigureAwait(false);
        return new AppUpdateCheckResult.Installed(installedResult);
    }
}
