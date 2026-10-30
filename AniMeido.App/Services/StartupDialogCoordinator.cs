using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services;

public sealed class StartupDialogCoordinator
{
    private readonly PrivacyService _privacy;
    private readonly DesktopSettingsStore _settings;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupDialogCoordinator(PrivacyService privacy, DesktopSettingsStore settings)
    {
        _privacy = privacy;
        _settings = settings;
    }

    /// <summary>只在隐私和公告都处理成功后报告完成；失败或取消的尝试允许重试。</summary>
    public async Task ShowStartupDialogsAsync(FrameworkElement root, CancellationToken cancellationToken = default)
    {
        await ShowPrivacyDialogAsync(root, cancellationToken);
        await ShowAnnouncementDialogAsync(root, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _completed.TrySetResult();
    }

    /// <summary>取消只结束本次等待，不改变启动弹窗的共享完成状态。</summary>
    public Task WaitForCompletionAsync(CancellationToken cancellationToken = default)
        => _completed.Task.WaitAsync(cancellationToken);

    public async Task ShowPrivacyDialogAsync(FrameworkElement root, CancellationToken cancellationToken = default)
    {
        if (await _privacy.IsAcceptedAsync(cancellationToken)) return;

        var dialog = new ContentDialog
        {
            Title = "隐私声明",
            Content = "AniMeido 会联网获取番剧资料、图片和更新信息。搜索与资料查询会向相应服务发送关键词、作品编号等必要参数；使用播放插件时，会访问你选择的播放源。\n\n"
                + "追番状态、个人评分、计划、笔记、浏览历史和截图保存在本机。当前应用不提供这些个人记录的云端同步。应用设置也保存在当前数据目录中。\n\n"
                + "播放源由第三方提供；在源站登录或完成验证时，相关信息按该网站的规则处理。请只安装和使用你信任的插件与播放源。",

            PrimaryButtonText = "同意",
            CloseButtonText = "拒绝",
            XamlRoot = root.XamlRoot
        };

        if (await ShowAsync(dialog, cancellationToken) == ContentDialogResult.Primary)
            await _privacy.AcceptAsync(cancellationToken);
        else
            Environment.Exit(0);
    }

    public async Task ShowAnnouncementDialogAsync(FrameworkElement root, CancellationToken cancellationToken = default)
    {
        if ((await _settings.LoadAsync(cancellationToken)).AnnouncementShown == true)
            return;

        var dialog = new ContentDialog
        {
            Title = "开发公告",
            Content = "AniMeido 正在持续开发中，功能会逐步完善。\n\n"
                    + "官方网站：animeido.com\n\n"
                    + "如果您有新想法或需要报告 Bug，请通过设置页的「关于」卡片中提供的联系方式与我们取得联系。",
            PrimaryButtonText = "知道了",
            XamlRoot = root.XamlRoot
        };

        await ShowAsync(dialog, cancellationToken);
        await _settings.UpdateAsync(value => value with { AnnouncementShown = true }, cancellationToken);
    }

    private static async Task<ContentDialogResult> ShowAsync(
        ContentDialog dialog,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var closing = cancellationToken.Register(() =>
            dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
