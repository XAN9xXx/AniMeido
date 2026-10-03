using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Serilog;

namespace AniMeido.App.Services;

/// <summary>更新检查启动任务。失败只记 Warning，不影响 App 启动。</summary>
internal static class UpdateStartupTask
{
    public static async Task CheckForUpdateSilentlyAsync(IServiceProvider provider, Window window)
    {
        try
        {
            if (provider.GetRequiredService<InstalledAppContext>().IsInstalled)
            {
                await CheckInstalledAsync(provider, window);
                return;
            }
            var result = await provider.GetRequiredService<UpdateService>().CheckForUpdateAsync();
            App.LatestVersion = result.LatestVersion;
            var decision = UpdatePromptPolicy.Decide(result, null, UpdatePromptTrigger.Startup);
            if (decision.LogWarning)
                Log.Warning("启动更新检查失败：{Status}，{Reason}", result.Status, result.FailureReason);
            if (!decision.ShouldPrompt)
                return;

            if (!window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (window.Content is FrameworkElement { XamlRoot: { } xamlRoot })
                    {
                        await UpdatePromptDialog.ShowAsync(result,
                            provider.GetRequiredService<DesktopSettingsStore>(),
                            UpdatePromptTrigger.Startup, xamlRoot);
                    }
                    else
                    {
                        Log.Warning("启动更新提示未显示：窗口内容尚未就绪");
                    }
                }
#pragma warning disable CA1031 // async void UI 回调必须自行兜底，不能让更新提示影响应用启动。
                catch (Exception ex)
                {
                    Log.Warning(ex, "启动更新提示失败，应用将继续运行");
                }
#pragma warning restore CA1031
            }))
            {
                Log.Warning("启动更新提示未显示：无法进入 UI 调度队列");
            }
        }
#pragma warning disable CA1031 // 更新检查与服务解析失败都不能阻断应用启动。
        catch (Exception ex)
        {
            Log.Warning(ex, "启动更新检查异常，应用将继续运行");
        }
#pragma warning restore CA1031
    }

    private static async Task CheckInstalledAsync(IServiceProvider provider, Window window)
    {
        var settings = await provider.GetRequiredService<DesktopSettingsStore>().LoadAsync();
        var service = provider.GetRequiredService<InstalledUpdateService>();
        var result = await service.CheckAsync(settings.UpdateChannel);
        if ((await provider.GetRequiredService<DesktopSettingsStore>().LoadAsync()).UpdateChannel != settings.UpdateChannel)
            return; // 用户在检查期间切换了通道，不用旧通道结果覆盖新选择。
        App.LatestVersion = result.LatestVersion;
        var decision = UpdatePromptPolicy.Decide(result, settings.SkippedUpdateVersion, UpdatePromptTrigger.Startup);
        if (decision.LogWarning)
            Log.Warning("启动安装更新检查失败：{Status}，{Reason}", result.Status, result.FailureReason);
        if (!decision.ShouldPrompt)
            return;
        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (window.Content is FrameworkElement { XamlRoot: { } root })
                    await UpdatePromptDialog.ShowAsync(result, service,
                        provider.GetRequiredService<DesktopSettingsStore>(), UpdatePromptTrigger.Startup, root);
                else
                    Log.Warning("启动安装更新提示未显示：窗口尚未就绪");
            }
#pragma warning disable CA1031 // async void 提示不得中断启动。
            catch (Exception ex)
            {
                Log.Warning(ex, "启动安装更新提示失败");
            }
#pragma warning restore CA1031
        }))
            Log.Warning("启动安装更新提示未显示：无法进入 UI 调度队列");
    }
}
