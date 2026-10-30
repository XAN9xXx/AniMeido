using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Serilog;

namespace AniMeido.App.Services;

/// <summary>启动流程拥有检查和提示的完整任务；退出时取消并等待，不遗留 UI 回调。</summary>
internal static class UpdateStartupTask
{
    public static async Task CheckForUpdateSilentlyAsync(
        IServiceProvider provider,
        Window window,
        Task startupNoticesCompleted,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var store = provider.GetRequiredService<DesktopSettingsStore>();
            if (provider.GetRequiredService<InstalledAppContext>().IsInstalled)
            {
                await CheckInstalledAsync(provider, store, window, startupNoticesCompleted, cancellationToken);
                return;
            }

            var result = await provider.GetRequiredService<UpdateService>()
                .CheckForUpdateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            App.LatestVersion = result.LatestVersion;
            var decision = UpdatePromptPolicy.Decide(result, null, UpdatePromptTrigger.Startup);
            if (decision.LogWarning)
            {
                Log.Warning("启动更新检查失败：{Status}，{Reason}", result.Status, result.FailureReason);
            }
            if (!decision.ShouldPrompt)
                return;

            await startupNoticesCompleted.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (window.Content is FrameworkElement { XamlRoot: { } root })
            {
                await UpdatePromptDialog.ShowAsync(
                    result, store, UpdatePromptTrigger.Startup, root, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // 可选更新提示失败不阻止应用启动，最终边界只记录一次。
        catch (Exception ex)
        {
            Log.Warning(ex, "启动更新检查或提示失败，应用将继续运行");
        }
#pragma warning restore CA1031
    }

    private static async Task CheckInstalledAsync(
        IServiceProvider provider,
        DesktopSettingsStore store,
        Window window,
        Task startupNoticesCompleted,
        CancellationToken cancellationToken)
    {
        var settings = await store.LoadAsync(cancellationToken);
        var service = provider.GetRequiredService<InstalledUpdateService>();
        var result = await service.CheckAsync(settings.UpdateChannel, cancellationToken);
        var currentSettings = await store.LoadAsync(cancellationToken);
        if (currentSettings.UpdateChannel != settings.UpdateChannel)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        App.LatestVersion = result.LatestVersion;
        var decision = UpdatePromptPolicy.Decide(
            result, currentSettings.SkippedUpdateVersion, UpdatePromptTrigger.Startup);
        if (decision.LogWarning)
        {
            Log.Warning(result.Failure, "启动安装更新检查失败：{Status}，{Reason}", result.Status, result.FailureReason);
        }
        if (!decision.ShouldPrompt)
            return;

        await startupNoticesCompleted.WaitAsync(cancellationToken);
        currentSettings = await store.LoadAsync(cancellationToken);
        if (currentSettings.UpdateChannel != settings.UpdateChannel)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        if (window.Content is FrameworkElement { XamlRoot: { } root })
        {
            await UpdatePromptDialog.ShowAsync(
                result, service, store, UpdatePromptTrigger.Startup, root, cancellationToken);
        }
    }
}
