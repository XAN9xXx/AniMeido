using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;
using AniMeido.PluginProtocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Views;

public sealed partial class HostedPluginSettingsPage : Page
{
    private readonly PluginContributionRegistry _contributions;
    private readonly HostedSettingsContribution _settings;
    private int _visit;
    private bool _busy;
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;

    public HostedPluginSettingsPage(PluginContributionRegistry contributions, HostedSettingsContribution settings)
    {
        _contributions = contributions; _settings = settings;
        InitializeComponent();
        TitleText.Text = settings.Title; PluginText.Text = settings.PluginDisplayName;
        Loaded += (_, _) => ++_visit;
        Unloaded += (_, _) => ++_visit;
    }
    private void OnOpenClick(object sender, RoutedEventArgs args)
    {
        if (_busy || !IsLoaded) return;
        _busy = true;
        PendingOperation = OpenAsync(_visit);
        DesktopUiWork.Track(PendingOperation);
    }
    private async Task OpenAsync(int visit)
    {
        OpenButton.IsEnabled = false;
        OpenProgress.Visibility = Visibility.Visible; OpenProgress.IsActive = true; StatusBar.IsOpen = false;
        try
        {
            await _contributions.OpenSettingsAsync(_settings.PluginId, _settings.SettingsId);
            if (visit != _visit || !IsLoaded) return;
            StatusBar.Severity = InfoBarSeverity.Success; StatusBar.Message = "插件设置窗口已打开。"; StatusBar.IsOpen = true;
        }
#pragma warning disable CA1031 // 独立插件调用的界面边界显示失败，并恢复入口。
        catch (Exception error)
        {
            if (visit == _visit && IsLoaded)
            { StatusBar.Severity = InfoBarSeverity.Error; StatusBar.Message = $"插件设置未能打开：{error.Message}"; StatusBar.IsOpen = true; }
        }
#pragma warning restore CA1031
        finally
        {
            _busy = false; OpenProgress.IsActive = false; OpenProgress.Visibility = Visibility.Collapsed; OpenButton.IsEnabled = true;
        }
    }
}
