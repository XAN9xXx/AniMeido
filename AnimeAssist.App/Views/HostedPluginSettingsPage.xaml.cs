using AniMeido.App.Services;
using AniMeido.PluginProtocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Views;

public sealed partial class HostedPluginSettingsPage : Page
{
    private readonly PluginContributionRegistry _contributions;
    private readonly HostedSettingsContribution _settings;

    public HostedPluginSettingsPage(
        PluginContributionRegistry contributions,
        HostedSettingsContribution settings)
    {
        _contributions = contributions;
        _settings = settings;
        InitializeComponent();
        TitleText.Text = settings.Title;
        PluginText.Text = settings.PluginDisplayName;
    }

    private async void OnOpenClick(object sender, RoutedEventArgs args)
    {
        OpenButton.IsEnabled = false;
        OpenProgress.Visibility = Visibility.Visible;
        OpenProgress.IsActive = true;
        StatusBar.IsOpen = false;
        try
        {
            await _contributions.OpenSettingsAsync(
                _settings.PluginId,
                _settings.SettingsId);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = "插件设置窗口已打开。";
            StatusBar.IsOpen = true;
        }
#pragma warning disable CA1031 // Optional plugin failures are reported in the settings surface.
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = $"无法打开插件设置：{ex.Message}";
            StatusBar.IsOpen = true;
        }
#pragma warning restore CA1031
        finally
        {
            OpenProgress.IsActive = false;
            OpenProgress.Visibility = Visibility.Collapsed;
            OpenButton.IsEnabled = true;
        }
    }
}
