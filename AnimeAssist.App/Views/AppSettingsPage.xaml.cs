using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using AniMeido.App.Services;
using System.Text.Json;

namespace AniMeido.App.Views
{
    public sealed partial class AppSettingsPage : Page
    {
        private readonly UpdateService _updateService;

        public AppSettingsPage(UpdateService updateService)
        {
            _updateService = updateService;
            InitializeComponent();

            var current = App.ThemeService.GetCurrentTheme();
            ThemeCombo.SelectedIndex = current switch
            {
                ElementTheme.Light => 0,
                ElementTheme.Dark => 1,
                _ => 2,
            };

            if (App.Plugins is not null)
            {
                foreach (var plugin in App.Plugins)
                    PluginList.Items.Add(plugin);
            }

            GitHubButton.SizeChanged += (s, e) =>
            {
                var visual = ElementCompositionPreview.GetElementVisual(GitHubButton);
                visual.CenterPoint = new System.Numerics.Vector3(
                    (float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            };

            WebsiteButton.SizeChanged += (s, e) =>
            {
                var visual = ElementCompositionPreview.GetElementVisual(WebsiteButton);
                visual.CenterPoint = new System.Numerics.Vector3(
                    (float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            };

            var currentVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version;
            if (currentVersion != null)
            {
                var curVer = $"v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}";
                var latestVer = App.LatestVersion != null ? $"v{App.LatestVersion}" : "--";
                VersionInfoText.Text = $"当前版本：{curVer} | 最新版本：{latestVer}";
            }
        }

        private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeCombo.SelectedIndex < 0) return;
            var theme = ThemeCombo.SelectedIndex switch
            {
                0 => ElementTheme.Light,
                1 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            App.ThemeService.SetTheme(theme);
        }

        private async void OnGitHubCardTapped(object sender, TappedRoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/XAN9xXx/AniMeido"));
        }

        private void OnGitHubPointerEntered(object sender, PointerRoutedEventArgs e) => AnimateButton(GitHubButton, 1.05f, 16);
        private void OnGitHubPointerExited(object sender, PointerRoutedEventArgs e) => AnimateButton(GitHubButton, 1.0f, 0);
        private void OnGitHubPointerPressed(object sender, PointerRoutedEventArgs e) => AnimateButton(GitHubButton, 0.95f, 16);
        private void OnGitHubPointerReleased(object sender, PointerRoutedEventArgs e) => AnimateButton(GitHubButton, 1.05f, 16);

        private async void OnWebsiteCardTapped(object sender, TappedRoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://animeido.com"));
        }

        private void OnWebsitePointerEntered(object sender, PointerRoutedEventArgs e) => AnimateButton(WebsiteButton, 1.05f, 16);
        private void OnWebsitePointerExited(object sender, PointerRoutedEventArgs e) => AnimateButton(WebsiteButton, 1.0f, 0);
        private void OnWebsitePointerPressed(object sender, PointerRoutedEventArgs e) => AnimateButton(WebsiteButton, 0.95f, 16);
        private void OnWebsitePointerReleased(object sender, PointerRoutedEventArgs e) => AnimateButton(WebsiteButton, 1.05f, 16);

        private async void OnUpdateCheckClick(object sender, RoutedEventArgs e)
        {
            UpdateCheckButton.IsEnabled = false;
            UpdateCheckButton.Content = "检查中…";
            try
            {
                var result = await _updateService.CheckForUpdateAsync();

                ContentDialog dialog;
                if (result.Status == UpdateCheckStatus.NetworkError || result.Status == UpdateCheckStatus.InvalidManifest)
                {
                    dialog = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = "检查更新失败，请稍后重试或检查网络连接。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                }
                else if (result.Status == UpdateCheckStatus.UpdateAvailable)
                {
                    dialog = new ContentDialog
                    {
                        Title = "发现新版本",
                        Content = $"最新版本：{result.LatestVersion}\n\n{result.ReleaseNotes}\n\n如果下载缓慢，请尝试使用 Motrix 等工具加速下载。",
                        PrimaryButtonText = "下载更新",
                        CloseButtonText = "稍后再说",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = this.XamlRoot
                    };
                    var dialogResult = await dialog.ShowAsync();
                    if (dialogResult == ContentDialogResult.Primary && result.DownloadUrl != null)
                    {
                        if (Uri.TryCreate(result.DownloadUrl, UriKind.Absolute, out var uri)
                            && uri.Scheme == Uri.UriSchemeHttps)
                        {
                            await Windows.System.Launcher.LaunchUriAsync(uri);
                        }
                    }
                    VersionInfoText.Text = $"当前版本：v{System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version?.Major}.{System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version?.Minor}.{System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version?.Build} | 最新版本：v{result.LatestVersion}";
                    return;
                }
                else if (result.Status == UpdateCheckStatus.NoUpdate)
                {
                    dialog = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = "已是最新版本。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    VersionInfoText.Text = $"当前版本：v{System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version?.Major}.{System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version?.Minor}.{System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version?.Build} | 最新版本：v{result.LatestVersion}";
                }
                else
                {
                    // IncompatibleClient 或其他未知状态
                    dialog = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = "检查更新失败，客户端兼容性问题。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                }
                await dialog.ShowAsync();
            }
            catch (HttpRequestException ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "检查更新",
                    Content = $"检查更新时发生网络错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (JsonException ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "检查更新",
                    Content = $"更新数据解析失败：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                UpdateCheckButton.IsEnabled = true;
                UpdateCheckButton.Content = "检查";
            }
        }

        private void AnimateButton(UIElement target, float scale, int zOffset)
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);
            var compositor = visual.Compositor;
            visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, zOffset));
            var sx = compositor.CreateScalarKeyFrameAnimation();
            sx.InsertKeyFrame(1.0f, scale); sx.Duration = TimeSpan.FromMilliseconds(200);
            var sy = compositor.CreateScalarKeyFrameAnimation();
            sy.InsertKeyFrame(1.0f, scale); sy.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Scale.X", sx);
            visual.StartAnimation("Scale.Y", sy);
        }
    }
}
