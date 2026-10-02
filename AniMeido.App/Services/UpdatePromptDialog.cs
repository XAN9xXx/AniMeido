using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace AniMeido.App.Services;

/// <summary>启动与设置页共用的更新提示，下载仅交给系统浏览器。</summary>
internal static class UpdatePromptDialog
{
    private static readonly SemaphoreSlim DialogGate = new(1, 1);

    public static async Task ShowAsync(
        UpdateService.UpdateCheckResult result,
        DesktopSettingsStore settingsStore,
        UpdatePromptTrigger trigger,
        XamlRoot xamlRoot)
    {
        // 两个入口共用一把锁，避免同一窗口同时打开两个 ContentDialog。
        await DialogGate.WaitAsync();
        try
        {
            string? skippedVersion = null;
            if (result.Status == UpdateCheckStatus.UpdateAvailable)
            {
                try
                {
                    skippedVersion = (await settingsStore.LoadAsync()).SkippedUpdateVersion;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "无法读取跳过版本设置，将照常显示更新提示");
                }
            }

            var decision = UpdatePromptPolicy.Decide(result, skippedVersion, trigger);
            if (decision.LogWarning)
                Log.Warning("更新检查失败：{Status}，{Reason}", result.Status, result.FailureReason);
            if (!decision.ShouldPrompt)
                return;

            var hasUpdate = result.Status is UpdateCheckStatus.UpdateAvailable or UpdateCheckStatus.UpdateRequired;
            var dialog = new ContentDialog
            {
                Title = result.Status switch
                {
                    UpdateCheckStatus.UpdateRequired => "当前版本过旧，需要更新",
                    UpdateCheckStatus.UpdateAvailable => "发现新版本",
                    _ => "检查更新",
                },
                Content = new ScrollViewer
                {
                    MaxHeight = 400,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new TextBlock
                    {
                        Text = BuildMessage(result, decision),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
                PrimaryButtonText = hasUpdate ? "下载更新" : string.Empty,
                SecondaryButtonText = decision.AllowSkip ? "跳过此版本" : string.Empty,
                CloseButtonText = hasUpdate ? "稍后再说" : "确定",
                DefaultButton = hasUpdate ? ContentDialogButton.Primary : ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };

            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Secondary && decision.AllowSkip)
            {
                await settingsStore.UpdateAsync(settings => settings with
                {
                    SkippedUpdateVersion = result.LatestVersion,
                });
            }
            else if (decision.CanResumeSkippedVersion)
            {
                // 手动重新查看后，下载或“稍后再说”恢复启动提示；再次点跳过则继续跳过。
                // 保存时复核版本，不能清掉其他入口刚写入的不同版本。
                await settingsStore.UpdateAsync(settings =>
                    UpdatePromptPolicy.IsSameVersion(settings.SkippedUpdateVersion, result.LatestVersion)
                        ? settings with { SkippedUpdateVersion = null }
                        : settings);
            }

            if (choice == ContentDialogResult.Primary && hasUpdate
                && Uri.TryCreate(result.DownloadUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps)
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                    Log.Warning("无法在浏览器中打开更新下载链接");
            }
        }
        finally
        {
            DialogGate.Release();
        }
    }

    public static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        await DialogGate.WaitAsync();
        try
        {
            await new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = xamlRoot,
            }.ShowAsync();
        }
        finally
        {
            DialogGate.Release();
        }
    }

    private static string BuildMessage(UpdateService.UpdateCheckResult result, UpdatePromptDecision decision)
    {
        if (result.Status == UpdateCheckStatus.NoUpdate)
            return $"已是最新版本。\n最新版本：{result.LatestVersion}";
        if (result.Status is not (UpdateCheckStatus.UpdateAvailable or UpdateCheckStatus.UpdateRequired))
            return result.FailureReason ?? result.Status switch
            {
                UpdateCheckStatus.NetworkError => "无法连接更新服务，请检查网络后重试。",
                UpdateCheckStatus.InvalidManifest => "更新清单无效，请稍后重试或联系维护者。",
                _ => "无法读取当前程序版本，无法判断是否需要更新。",
            };

        var message = new StringBuilder();
        if (result.Status == UpdateCheckStatus.UpdateRequired)
            message.AppendLine("当前版本过旧，需要更新。此更新不能跳过，请下载安装最新版本。").AppendLine();
        message.AppendLine($"最新版本：{result.LatestVersion}");
        if (result.Size is { } size)
            message.AppendLine($"安装包大小：{size.ToString("N0", CultureInfo.InvariantCulture)} 字节");
        if (result.ReleaseDate is { } date)
            message.AppendLine($"发布日期：{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        message.AppendLine().AppendLine("更新说明：")
            .AppendLine(string.IsNullOrWhiteSpace(result.ReleaseNotes) ? "未提供更新说明。" : result.ReleaseNotes);
        if (decision.CanResumeSkippedVersion)
            message.AppendLine().AppendLine("此版本之前已跳过。选择“下载更新”或“稍后再说”将取消跳过，恢复下次启动提示；选择“跳过此版本”则继续跳过。");
        message.AppendLine().Append("下载将通过浏览器打开，你需要自行运行安装包。");
        return message.ToString();
    }
}
