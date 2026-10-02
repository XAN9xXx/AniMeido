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
        private readonly PluginHostSupervisor _pluginHostSupervisor;
        private readonly DesktopSettingsStore _desktopSettings;
        private bool _loadingDesktopSettings;
        private bool _isStatusSubscribed;

        public AppSettingsPage(
            UpdateService updateService,
            PluginPackageManager pluginPackageManager,
            PluginHostSupervisor pluginHostSupervisor,
            DesktopSettingsStore desktopSettings)
        {
            _updateService = updateService;
            _pluginPackageManager = pluginPackageManager;
            _pluginHostSupervisor = pluginHostSupervisor;
            _desktopSettings = desktopSettings;
            InitializeComponent();
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;

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
            if (!_isStatusSubscribed)
            {
                _pluginHostSupervisor.StatusChanged += OnPluginHostStatusChanged;
                _isStatusSubscribed = true;
            }

            try
            {
                _loadingDesktopSettings = true;
                KeepInTrayToggle.IsOn = (await _desktopSettings.LoadAsync())
                    .KeepInTrayOnClose;
                await RefreshInstalledPluginsAsync();
            }
            catch (Exception ex) when (
                ex is PluginOperationException
                    or IOException
                    or UnauthorizedAccessException
                    or JsonException
                    or InvalidOperationException)
            {
                await ShowPluginMessageAsync("无法读取设置", ex.Message);
            }
            finally
            {
                _loadingDesktopSettings = false;
            }
        }

        private async void OnKeepInTrayToggled(
            object sender,
            RoutedEventArgs e)
        {
            if (_loadingDesktopSettings)
            {
                return;
            }

            if (App.Current is App app)
            {
                var requestedValue = KeepInTrayToggle.IsOn;
                try
                {
                    await app.SetKeepInTrayOnCloseAsync(requestedValue);
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException)
                {
                    _loadingDesktopSettings = true;
                    KeepInTrayToggle.IsOn = !requestedValue;
                    _loadingDesktopSettings = false;
                    await ShowPluginMessageAsync(
                        "无法更改托盘设置",
                        ex.Message);
                }
            }
        }

        private async Task RefreshInstalledPluginsAsync()
        {
            var plugins = await _pluginPackageManager.GetInstalledPluginsAsync();
            InstalledPluginList.ItemsSource = plugins;
            NoInstalledPluginsText.Visibility = plugins.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            PluginReloadInfoBar.IsOpen = _pluginPackageManager.RestartRequired;
            PluginHostStatusText.Text =
                $"插件状态：{_pluginHostSupervisor.StatusText}";
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
                    $"{result.DisplayName} {result.Version} 已安装。");
                await ApplyPendingPluginChangesAsync();
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
                "插件已启用。");

        private async void OnDisablePluginClick(object sender, RoutedEventArgs e)
            => await RunPluginActionAsync(
                sender,
                (pluginId) => _pluginPackageManager.SetEnabledAsync(pluginId, false),
                "插件已禁用。");

        private async void OnRollbackPluginClick(object sender, RoutedEventArgs e)
            => await RunPluginActionAsync(
                sender,
                (pluginId) => _pluginPackageManager.RollbackAsync(pluginId),
                "插件已切换到上一版本。");

        private async void OnUninstallPluginClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string pluginId })
            {
                return;
            }

            var confirmation = new ContentDialog
            {
                Title = "卸载插件",
                Content = "重载插件后会删除插件文件，插件保存的数据会保留。",
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
                "插件已卸载。");
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
                if (await ApplyPendingPluginChangesAsync())
                {
                    await ShowPluginMessageAsync("插件管理", successMessage);
                }
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

        private async void OnReloadPluginsClick(
            object sender,
            RoutedEventArgs e)
            => await ApplyPendingPluginChangesAsync(forcePrompt: true);

        private async Task<bool> ApplyPendingPluginChangesAsync(
            bool forcePrompt = false)
        {
            var hasActiveUi = await _pluginHostSupervisor
                .HasActivePluginUiAsync();
            if (!IsLoaded)
            {
                return false;
            }
            if (hasActiveUi || forcePrompt)
            {
                var confirmation = new ContentDialog
                {
                    Title = "重载插件",
                    Content = hasActiveUi
                        ? "正在运行的插件窗口或播放将被关闭。是否现在重载？"
                        : "是否立即重载可选插件？",
                    PrimaryButtonText = "重载",
                    CloseButtonText = "稍后",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                if (await confirmation.ShowAsync()
                    != ContentDialogResult.Primary)
                {
                    await RefreshInstalledPluginsAsync();
                    return false;
                }
            }

            try
            {
                await _pluginHostSupervisor.ReloadAsync();
                await RefreshInstalledPluginsAsync();
                return true;
            }
            catch (Exception ex) when (
                ex is PluginOperationException
                or IOException
                or InvalidOperationException
                or TimeoutException)
            {
                await RefreshInstalledPluginsAsync();
                await ShowPluginMessageAsync(
                    "插件重载失败",
                    ex.Message);
                return false;
            }
        }

        private void OnPluginHostStatusChanged(
            object? sender,
            EventArgs e)
            => DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLoaded)
                {
                    PluginHostStatusText.Text =
                        $"插件状态：{_pluginHostSupervisor.StatusText}";
                }
            });

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            if (_isStatusSubscribed)
            {
                _pluginHostSupervisor.StatusChanged -=
                    OnPluginHostStatusChanged;
                _isStatusSubscribed = false;
            }
        }

        private async Task ShowPluginMessageAsync(string title, string message)
        {
            if (!IsLoaded || XamlRoot is null)
            {
                return;
            }
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
                App.LatestVersion = result.LatestVersion;
                // 等待 HTTP 时可能已经离开设置页，不在失效的 XamlRoot 上显示弹窗。
                if (!IsLoaded || XamlRoot is null)
                    return;
                var current = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
                VersionInfoText.Text = $"当前版本：{(current is null ? "未知" : $"v{current.ToString(3)}")} | 最新版本：{(result.LatestVersion is null ? "--" : $"v{result.LatestVersion}")}";
                await UpdatePromptDialog.ShowAsync(result, _desktopSettings,
                    UpdatePromptTrigger.Manual, XamlRoot);
            }
#pragma warning disable CA1031 // UI 事件兜底：检查、偏好保存或浏览器启动失败都只提示并继续运行。
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "手动更新检查或提示失败");
                if (IsLoaded && XamlRoot is { } xamlRoot)
                {
                    try
                    {
                        await UpdatePromptDialog.ShowMessageAsync(xamlRoot, "检查更新失败", ex.Message);
                    }
                    catch (Exception dialogException)
                    {
                        Serilog.Log.Warning(dialogException, "无法显示手动更新错误提示");
                    }
                }
            }
#pragma warning restore CA1031
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
