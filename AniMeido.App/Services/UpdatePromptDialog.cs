using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace AniMeido.App.Services;

/// <summary>启动与设置页共用：安装模式应用内更新，其余模式交给浏览器。</summary>
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

    public static async Task ShowAsync(
        InstalledUpdateResult result,
        InstalledUpdateService service,
        DesktopSettingsStore settingsStore,
        UpdatePromptTrigger trigger,
        XamlRoot xamlRoot)
    {
        await DialogGate.WaitAsync();
        try
        {
            var settings = await settingsStore.LoadAsync();
            var decision = UpdatePromptPolicy.Decide(result, settings.SkippedUpdateVersion, trigger);
            if (decision.LogWarning)
                Log.Warning("安装更新检查失败：{Status}，{Reason}", result.Status, result.FailureReason);
            if (!decision.ShouldPrompt)
                return;

            var ready = result.Status == InstalledUpdateStatus.PendingReady;
            var downloadable = result.Status is InstalledUpdateStatus.UpdateAvailable or InstalledUpdateStatus.PreviouslyFailed;
            var reinstall = result.Status == InstalledUpdateStatus.ReinstallRequired;
            var busy = false;
            var restartRequested = false;
            var message = new TextBlock { Text = BuildInstalledMessage(result, decision), TextWrapping = TextWrapping.Wrap };
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
            var progressText = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(message);
            content.Children.Add(progressBar);
            content.Children.Add(progressText);
            var dialog = new ContentDialog
            {
                Title = ready ? "更新已就绪" : "检查更新",
                Content = new ScrollViewer { MaxHeight = 400, Content = content },
                PrimaryButtonText = ready ? "立即重启" : downloadable ? "下载并更新" : reinstall ? "打开官网" : "",
                SecondaryButtonText = decision.AllowSkip ? "跳过此版本" : "",
                CloseButtonText = ready ? "下次启动时更新" : downloadable || reinstall ? "稍后再说" : "确定",
                DefaultButton = ready || downloadable ? ContentDialogButton.Primary : ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };
            CancellationTokenSource? downloadCancellation = null;
            dialog.Closing += (_, args) =>
            {
                if (busy)
                {
                    args.Cancel = true;
                    downloadCancellation?.Cancel();
                }
            };
            dialog.PrimaryButtonClick += (sender, args) =>
            {
                if (!ready && !downloadable)
                    return;
                args.Cancel = true;
                // 不用长时间的按钮 deferral，避免 WinUI 在下载期间禁用“取消”。
                if (!busy)
                    _ = HandlePrimaryAsync();
            };
            async Task HandlePrimaryAsync()
            {
                try
                {
                    if (ready)
                    {
                        if (Application.Current is not App app)
                            throw new InvalidOperationException("应用退出入口不可用。");
                        service.Restart(() =>
                        {
                            restartRequested = true;
                            dialog.Hide();
                            app.RequestExit();
                        });
                        return;
                    }
                    busy = true;
                    dialog.IsPrimaryButtonEnabled = false;
                    downloadCancellation = new CancellationTokenSource();
                    // 下载前取消跳过；立即重启时不会在释放 DI 后再访问设置存储。
                    await ResumeSkippedAsync(settingsStore, result.LatestVersion, decision);
                    dialog.Title = "正在准备更新";
                    dialog.PrimaryButtonText = "";
                    dialog.SecondaryButtonText = "";
                    dialog.CloseButtonText = "取消";
                    message.Text = "下载中；取消后保留未完成的包供下次续传。下载完成后将校验并解压。";
                    progressBar.Visibility = progressText.Visibility = Visibility.Visible;
                    var total = result.Manifest!.Package.Size;
                    progressBar.Value = 0;
                    progressText.Text = $"0 / {total:N0} 字节";
                    var progress = new Progress<long>(bytes =>
                    {
                        if (!busy)
                            return;
                        progressBar.Value = (double)bytes / total * 100;
                        progressText.Text = $"{bytes:N0} / {total:N0} 字节";
                        if (bytes == total)
                            message.Text = "正在校验并解压更新；收尾阶段不可取消。";
                    });
                    await service.PrepareAsync(result.Manifest, progress, downloadCancellation.Token);
                    ready = true;
                    dialog.Title = "更新已就绪";
                    message.Text = $"版本 {result.LatestVersion} 已下载并准备好，重启即可更新。";
                    dialog.PrimaryButtonText = "立即重启";
                    dialog.CloseButtonText = "下次启动时更新";
                    progressBar.Visibility = progressText.Visibility = Visibility.Collapsed;
                }
                catch (OperationCanceledException ex)
                {
                    Log.Warning(ex, "用户取消了应用更新下载");
                    message.Text = "下载已取消，未完成的包已保留。可以重试续传或关闭。";
                    dialog.PrimaryButtonText = "重试";
                    dialog.CloseButtonText = "关闭";
                }
#pragma warning disable CA1031 // 模态对话框事件必须显示失败并允许重试，不让 async void 异常逃逸。
                catch (Exception ex)
                {
                    Log.Warning(ex, "应用更新操作失败");
                    message.Text = $"更新失败：{ex.Message}\n安装路径：{service.InstallRoot}\n可以重试或关闭，当前版本未切换。";
                    dialog.PrimaryButtonText = ready ? "重试立即重启" : "重试";
                    dialog.CloseButtonText = "关闭";
                }
#pragma warning restore CA1031
                finally
                {
                    busy = false;
                    downloadCancellation?.Dispose();
                    downloadCancellation = null;
                    dialog.IsPrimaryButtonEnabled = true;
                }
            }

            var choice = await dialog.ShowAsync();
            if (restartRequested)
                return;
            if (choice == ContentDialogResult.Secondary && decision.AllowSkip)
                await settingsStore.UpdateAsync(value => value with { SkippedUpdateVersion = result.LatestVersion });
            else
                await ResumeSkippedAsync(settingsStore, result.LatestVersion, decision);
            if (choice == ContentDialogResult.Primary && reinstall)
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("https://animeido.com")))
                    Log.Warning("无法在浏览器中打开官网");
            }
        }
        finally
        {
            DialogGate.Release();
        }
    }

    private static Task ResumeSkippedAsync(DesktopSettingsStore store, string? version, UpdatePromptDecision decision)
        => decision.CanResumeSkippedVersion
            ? store.UpdateAsync(settings => UpdatePromptPolicy.IsSameVersion(settings.SkippedUpdateVersion, version)
                ? settings with { SkippedUpdateVersion = null } : settings)
            : Task.CompletedTask;

    private static string BuildInstalledMessage(InstalledUpdateResult result, UpdatePromptDecision decision)
    {
        if (result.Status == InstalledUpdateStatus.PendingReady)
            return $"版本 {result.PendingVersion} 已下载，重启即可更新。";
        if (result.Status == InstalledUpdateStatus.NoUpdate)
            return "已是最新版本。稳定版未追上当前预览版时，不会降级。";
        if (result.Manifest is not { } manifest)
            return result.FailureReason ?? "无法检查更新，请稍后重试。";
        var message = new StringBuilder();
        if (result.Status == InstalledUpdateStatus.ReinstallRequired)
            message.AppendLine("当前安装版本过旧，无法直接更新，请到官网重新安装。").AppendLine();
        if (result.Status == InstalledUpdateStatus.PreviouslyFailed)
            message.AppendLine("该版本曾在本机启动失败并已回滚。仍可下载重试，请谨慎选择。").AppendLine();
        message.AppendLine($"最新版本：{manifest.Version}")
            .AppendLine($"更新包大小：{manifest.Package.Size:N0} 字节")
            .AppendLine($"发布日期：{manifest.ReleaseDate:yyyy-MM-dd}")
            .AppendLine().AppendLine("更新说明：")
            .AppendLine(string.IsNullOrWhiteSpace(manifest.Notes) ? "未提供更新说明。" : manifest.Notes);
        if (decision.CanResumeSkippedVersion)
            message.AppendLine().Append("此版本之前已跳过。下载或稍后再说将取消跳过；再次点跳过则继续跳过。");
        return message.ToString();
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

    public static async Task<bool> ConfirmAsync(XamlRoot xamlRoot, string title, string message, string confirmText)
    {
        await DialogGate.WaitAsync();
        try
        {
            return await new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = confirmText,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            }.ShowAsync() == ContentDialogResult.Primary;
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
