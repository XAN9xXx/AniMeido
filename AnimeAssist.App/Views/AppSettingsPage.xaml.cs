using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using AniMeido.App.Services;
using System.Text.Json;
using Windows.Storage.Pickers;

namespace AniMeido.App.Views
{
    public sealed partial class AppSettingsPage : Page
    {
        private readonly UpdateService _updateService;
        private readonly PluginPackageManager _pluginPackageManager;

        public AppSettingsPage(
            UpdateService updateService,
            PluginPackageManager pluginPackageManager)
        {
            _updateService = updateService;
            _pluginPackageManager = pluginPackageManager;
            InitializeComponent();
            Loaded += OnPageLoaded;

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

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnPageLoaded;
            try
            {
                await RefreshInstalledPluginsAsync();
            }
            catch (PluginOperationException ex)
            {
                await ShowPluginMessageAsync("无法读取插件状态", ex.Message);
            }
        }

        private async Task RefreshInstalledPluginsAsync()
        {
            var plugins = await _pluginPackageManager.GetInstalledPluginsAsync();
            InstalledPluginList.ItemsSource = plugins;
            NoInstalledPluginsText.Visibility = plugins.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            PluginRestartInfoBar.IsOpen = _pluginPackageManager.RestartRequired;
        }

        private async void OnInstallPluginClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".animeido-plugin");

            if (App.MainWindow is not Window window)
            {
                await ShowPluginMessageAsync("安装插件", "主窗口尚未就绪。");
                return;
            }

            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            InstallPluginButton.IsEnabled = false;
            try
            {
                var result = await _pluginPackageManager.InstallPackageAsync(file.Path);
                var action = result.IsUpgrade ? "升级" : "安装";
                await RefreshInstalledPluginsAsync();
                await ShowPluginMessageAsync(
                    $"{action}完成",
                    $"{result.DisplayName} {result.Version} 已准备完成，重启 AniMeido 后生效。");
            }
            catch (PluginOperationException ex)
            {
                await ShowPluginMessageAsync("无法安装插件", ex.Message);
            }
            finally
            {
                InstallPluginButton.IsEnabled = true;
            }
        }

        private async void OnEnablePluginClick(object sender, RoutedEventArgs e)
            => await RunPluginActionAsync(
                sender,
                (pluginId) => _pluginPackageManager.SetEnabledAsync(pluginId, true),
                "插件将在重启后启用。");

        private async void OnDisablePluginClick(object sender, RoutedEventArgs e)
            => await RunPluginActionAsync(
                sender,
                (pluginId) => _pluginPackageManager.SetEnabledAsync(pluginId, false),
                "插件将在重启后禁用。");

        private async void OnRollbackPluginClick(object sender, RoutedEventArgs e)
            => await RunPluginActionAsync(
                sender,
                (pluginId) => _pluginPackageManager.RollbackAsync(pluginId),
                "插件将在重启后切换到上一版本。");

        private async void OnUninstallPluginClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string pluginId })
            {
                return;
            }

            var confirmation = new ContentDialog
            {
                Title = "卸载插件",
                Content = "插件文件将在重启 AniMeido 时删除。插件自行保存的用户数据不会自动删除。",
                PrimaryButtonText = "卸载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await RunPluginActionAsync(
                sender,
                (id) => _pluginPackageManager.RequestUninstallAsync(id),
                "插件将在重启后卸载。");
        }

        private async Task RunPluginActionAsync(
            object sender,
            Func<string, Task> action,
            string successMessage)
        {
            if (sender is not Button { Tag: string pluginId } button)
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                await action(pluginId);
                await RefreshInstalledPluginsAsync();
                await ShowPluginMessageAsync("插件管理", successMessage);
            }
            catch (PluginOperationException ex)
            {
                await ShowPluginMessageAsync("插件操作失败", ex.Message);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private async Task ShowPluginMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
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
            // 延迟到下一帧切换主题，让 ComboBox 弹出层/动画先完成收尾
            DispatcherQueue.TryEnqueue(() => App.ThemeService.SetTheme(theme));
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
