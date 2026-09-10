using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services;

/// <summary>
/// 启动弹窗协调器：隐私声明 + 开发公告。
/// </summary>
public sealed class StartupDialogCoordinator
{
    private readonly FrameworkElement _xamlRootProvider;

    public StartupDialogCoordinator(FrameworkElement xamlRootProvider)
    {
        _xamlRootProvider = xamlRootProvider;
    }

    /// <summary>显示隐私声明弹窗。</summary>
    public async Task ShowPrivacyDialogAsync()
    {
        if (App.PrivacyService.IsAccepted()) return;

        var dialog = new ContentDialog
        {
            Title = "隐私声明",
            Content = "AniMeido 仅向 Bangumi API 请求番剧数据，不收集或发送任何个人信息。当前应用会在本地存储追番记录、浏览历史、收藏标签和拖放配置等数据（%AppData%/AniMeido/AniMeido.db），不会上传到外部服务器。后续某些插件的功能可能涉及隐私问题，此类插件安装后，将在初次启动时以弹窗提醒。",

            PrimaryButtonText = "同意",
            CloseButtonText = "拒绝",
            XamlRoot = _xamlRootProvider.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            App.PrivacyService.Accept();
        else
            Environment.Exit(0);
    }

    /// <summary>显示开发公告弹窗。</summary>
    public async Task ShowAnnouncementDialogAsync()
    {
        const string regKey = @"HKEY_CURRENT_USER\Software\AniMeido";
        if (Microsoft.Win32.Registry.GetValue(regKey, "AnnouncementShown", null) is 1)
            return;

        var dialog = new ContentDialog
        {
            Title = "开发公告",
            Content = "AniMeido 正在持续开发中，功能会逐步完善。\n\n"
                    + "官方网站：animeido.com\n\n"
                    + "如果您有新想法或需要报告 Bug，请通过设置页的「关于」卡片中提供的联系方式与我们取得联系。",
            PrimaryButtonText = "知道了",
            XamlRoot = _xamlRootProvider.XamlRoot
        };

        await dialog.ShowAsync();
        Microsoft.Win32.Registry.SetValue(regKey, "AnnouncementShown", 1);
    }
}
