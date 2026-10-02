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
}
