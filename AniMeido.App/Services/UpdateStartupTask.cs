using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services;

/// <summary>
/// 更新检查启动任务。在 App 启动后静默检查更新。
/// </summary>
internal static class UpdateStartupTask
{
    /// <summary>静默检查更新，发现新版本时弹出更新提示。</summary>
    public static async Task CheckForUpdateSilentlyAsync(IServiceProvider provider, Window window)
    {
        try
        {
            var updateService = provider.GetRequiredService<UpdateService>();
            var result = await updateService.CheckForUpdateAsync();
            App.LatestVersion = result.LatestVersion;
            if (result.Status != UpdateCheckStatus.UpdateAvailable) return;

            window.DispatcherQueue.TryEnqueue(async () =>
            {
                if (window.Content is FrameworkElement fe && fe.XamlRoot is { } xamlRoot)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "发现新版本",
                        Content = $"最新版本：{result.LatestVersion}\n\n{result.ReleaseNotes}",
                        PrimaryButtonText = "下载更新",
                        CloseButtonText = "稍后再说",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = xamlRoot
                    };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary && result.DownloadUrl != null)
                    {
                        if (Uri.TryCreate(result.DownloadUrl, UriKind.Absolute, out var uri)
                            && uri.Scheme == Uri.UriSchemeHttps)
                        {
                            await Windows.System.Launcher.LaunchUriAsync(uri);
                        }
                    }
                }
            });
        }
#pragma warning disable CA1031 // 更新检查静默失败，不影响启动
        catch
        {
            // 更新检查静默失败，不影响启动
        }
#pragma warning restore CA1031
    }
}
