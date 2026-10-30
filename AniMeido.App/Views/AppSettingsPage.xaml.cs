using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using AniMeido.App.Services;
using System.Text.Json;
using Microsoft.Windows.Storage.Pickers;

namespace AniMeido.App.Views
{
    public sealed partial class AppSettingsPage : Page
    {
        private readonly AppUpdateCheckService _updateChecks;
        private readonly InstalledUpdateService _installedUpdates;
        private readonly InstalledAppContext _installedApp;
        private readonly RollbackDataStore _rollbackData;
        private CancellationTokenSource? _updateCheckCancellation;
        private readonly PluginManagementService _plugins;
        private readonly DesktopSettingsStore _desktopSettings;
        private readonly ThemeService _themeService;
        private bool _loadingTheme = true;
        private bool _loadingDesktopSettings;
        private bool _isStatusSubscribed;
        private int _navigationVersion;

        public AppSettingsPage(
            AppUpdateCheckService updateChecks,
            PluginManagementService plugins,
            DesktopSettingsStore desktopSettings,
            ThemeService themeService,
            InstalledUpdateService installedUpdates,
            InstalledAppContext installedApp,
            RollbackDataStore rollbackData)
        {
            _updateChecks = updateChecks;
            _installedUpdates = installedUpdates;
            _installedApp = installedApp;
            _rollbackData = rollbackData;
            _plugins = plugins;
            _desktopSettings = desktopSettings;
            _themeService = themeService;
            InitializeComponent();
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;

            var current = _themeService.GetCurrentTheme();
            ThemeCombo.SelectedIndex = current switch
            {
                ElementTheme.Light => 0,
                ElementTheme.Dark => 1,
                _ => 2,
            };

            _loadingTheme = false;
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

            UpdateChannelPanel.Visibility = installedApp.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
            LegacyUpdateText.Visibility = installedApp.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
            if (installedApp.IsInstalled)
            {
                RefreshVersionText();
                return;
            }
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
            var navigationVersion = _navigationVersion;
            if (!_isStatusSubscribed)
            {
                _plugins.StatusChanged += OnPluginHostStatusChanged;
                _isStatusSubscribed = true;
            }

            try
            {
                _loadingDesktopSettings = true;
                var settings = await _desktopSettings.LoadAsync();
                if (navigationVersion != _navigationVersion || !IsLoaded)
                    return;
                KeepInTrayToggle.IsOn = settings.KeepInTrayOnClose;
                UpdateChannelCombo.SelectedIndex = settings.UpdateChannel == "preview" ? 1 : 0;
                await RefreshRollbackDataAsync();
                if (navigationVersion != _navigationVersion || !IsLoaded)
                    return;
                await RefreshInstalledPluginsAsync();
            }
            catch (Exception ex) when (
                ex is PluginOperationException
                    or IOException
                    or UnauthorizedAccessException
                    or JsonException
                    or InvalidOperationException)
            {
                Serilog.Log.Warning(ex, "无法读取设置");
                if (navigationVersion == _navigationVersion)
                    await ShowPluginMessageAsync("无法读取设置", ex.Message);
            }
            finally
            {
                if (navigationVersion == _navigationVersion)
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
            var navigationVersion = _navigationVersion;
            var state = await _plugins.GetStateAsync();
            if (navigationVersion != _navigationVersion || !IsLoaded)
                return;

            InstalledPluginList.ItemsSource = state.Plugins;
            NoInstalledPluginsText.Visibility = state.Plugins.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            PluginReloadInfoBar.IsOpen = state.ReloadRequired;
            PluginHostStatusText.Text =
                $"插件状态：{state.HostStatus}";
        }

        private async void OnInstallPluginClick(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is not Window window)
            {
                await ShowPluginMessageAsync("安装插件", "主窗口尚未就绪。");
                return;
            }

            var navigationVersion = _navigationVersion;
            var packageSelected = false;
            InstallPluginButton.IsEnabled = false;
            try
            {
                // 桌面选择器支持管理员进程，不使用受 UWP 权限限制的旧入口。
                var picker = new FileOpenPicker(window.AppWindow.Id);
                picker.FileTypeFilter.Add(".animeido-plugin");
                var file = await picker.PickSingleFileAsync();
                if (file is null || navigationVersion != _navigationVersion || !IsLoaded)
                {
                    return;
                }

                packageSelected = true;
                var result = await _plugins.InstallAsync(file.Path);
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
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Error(ex, "插件安装未完成，阶段：{Stage}", packageSelected ? "安装" : "选择文件");
                if (navigationVersion == _navigationVersion)
                {
                    await ShowPluginMessageAsync(
                        "无法安装插件",
                        packageSelected
                            ? "插件安装操作未能完成，请重新打开设置页确认安装状态。"
                            : "无法打开文件选择窗口。请稍后重试；若仍失败，请查看错误日志。");
                }
            }
            finally
            {
                InstallPluginButton.IsEnabled = true;
            }
        }

        private async void OnEnablePluginClick(object sender, RoutedEventArgs e)
            => await RunPluginActionAsync(
                sender,
                PluginChange.Enable,
                "插件已启用。");

        private async void OnDisablePluginClick(object sender, RoutedEventArgs e)
            => await RunPluginActionAsync(
                sender,
                PluginChange.Disable,
                "插件已禁用。");

        private async void OnRollbackPluginClick(object sender, RoutedEventArgs e)
            => await RunPluginActionAsync(
                sender,
                PluginChange.Rollback,
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
                PluginChange.Uninstall,
                "插件已卸载。");
        }

        private async Task RunPluginActionAsync(
            object sender,
            PluginChange change,
            string successMessage)
        {
            if (sender is not Button { Tag: string pluginId } button)
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                await _plugins.ApplyChangeAsync(pluginId, change);
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
            var navigationVersion = _navigationVersion;
            var result = await _plugins.ApplyPendingAsync(
                prompt => ConfirmPluginReloadAsync(prompt, navigationVersion), forcePrompt);
            await RefreshInstalledPluginsAsync();
            if (result.FailureMessage is { } message)
            {
                await ShowPluginMessageAsync("插件重载失败", message);
            }
            return result.Applied;
        }

        private async Task<bool> ConfirmPluginReloadAsync(PluginReloadPrompt prompt, int navigationVersion)
        {
            if (navigationVersion != _navigationVersion || !IsLoaded || XamlRoot is null)
                return false;
            if (!prompt.RequiresConfirmation)
                return true;

            var confirmation = new ContentDialog
            {
                Title = "重载插件",
                Content = prompt.HasActiveUi
                    ? "正在运行的插件窗口或播放将被关闭。是否现在重载？"
                    : "是否立即重载可选插件？",
                PrimaryButtonText = "重载",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            var result = await confirmation.ShowAsync();
            return result == ContentDialogResult.Primary
                && navigationVersion == _navigationVersion && IsLoaded;
        }

        private void OnPluginHostStatusChanged(
            object? sender,
            EventArgs e)
            => DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLoaded)
                {
                    PluginHostStatusText.Text =
                        $"插件状态：{_plugins.HostStatus}";
                }
            });

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            _navigationVersion++;
            _updateCheckCancellation?.Cancel();
            if (_isStatusSubscribed)
            {
                _plugins.StatusChanged -=
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
            if (_loadingTheme || ThemeCombo.SelectedIndex < 0) return;
            var theme = ThemeCombo.SelectedIndex switch
            {
                0 => ElementTheme.Light,
                1 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            // 延迟到下一帧切换主题，让 ComboBox 弹出层/动画先完成收尾
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (!IsLoaded)
                {
                    return;
                }
                try
                {
                    await _themeService.SetThemeAsync(theme);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Serilog.Log.Error(ex, "保存主题设置失败");
                    _loadingTheme = true;
                    ThemeCombo.SelectedIndex = _themeService.GetCurrentTheme() switch
                    {
                        ElementTheme.Light => 0,
                        ElementTheme.Dark => 1,
                        _ => 2,
                    };
                    _loadingTheme = false;
                    if (IsLoaded)
                    {
                        await ShowPluginMessageAsync("主题未保存", "无法保存主题设置，请稍后重试。");
                    }
                }
            });
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
            => await CheckForUpdatesAsync();

        private async Task CheckForUpdatesAsync()
        {
            if (_updateCheckCancellation is not null)
                return;
            using var cancellation = new CancellationTokenSource();
            _updateCheckCancellation = cancellation;
            var navigationVersion = _navigationVersion;
            UpdateChannelCombo.IsEnabled = false;
            UpdateCheckButton.IsEnabled = false;
            UpdateCheckButton.Content = "检查中…";
            try
            {
                var result = await _updateChecks.CheckAsync(cancellation.Token);
                if (navigationVersion != _navigationVersion || !IsLoaded
                    || XamlRoot is null || cancellation.IsCancellationRequested)
                    return;

                App.LatestVersion = result.LatestVersion;
                RefreshVersionText();
                switch (result)
                {
                    case AppUpdateCheckResult.Installed installed:
                        await UpdatePromptDialog.ShowAsync(installed.Result, _installedUpdates, _desktopSettings,
                            UpdatePromptTrigger.Manual, XamlRoot, cancellation.Token);
                        break;
                    case AppUpdateCheckResult.Legacy legacy:
                        await UpdatePromptDialog.ShowAsync(legacy.Result, _desktopSettings,
                            UpdatePromptTrigger.Manual, XamlRoot, cancellation.Token);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
#pragma warning disable CA1031 // UI 事件兜底：检查、偏好保存或浏览器启动失败都只提示并继续运行。
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "手动更新检查或提示失败");
                if (navigationVersion == _navigationVersion && IsLoaded && XamlRoot is { } xamlRoot)
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
                _updateCheckCancellation = null;
                UpdateChannelCombo.IsEnabled = true;
                UpdateCheckButton.IsEnabled = true;
                UpdateCheckButton.Content = "检查";
            }
        }

        private void RefreshVersionText()
        {
            var version = _installedApp.IsInstalled ? _installedApp.Version
                : System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);
            VersionInfoText.Text = $"当前版本：v{version ?? "未知"} | 最新版本：{(App.LatestVersion is null ? "--" : $"v{App.LatestVersion}")}";
        }

        private async void OnUpdateChannelSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingDesktopSettings || !IsLoaded || !_installedApp.IsInstalled || UpdateChannelCombo.SelectedIndex < 0)
                return;
            var channel = UpdateChannelCombo.SelectedIndex == 1 ? "preview" : "stable";
            string? previousChannel = null;
            UpdateChannelCombo.IsEnabled = false;
            try
            {
                var oldSettings = await _desktopSettings.LoadAsync();
                previousChannel = oldSettings.UpdateChannel;
                await _desktopSettings.UpdateAsync(settings => settings with { UpdateChannel = channel });
                if (IsLoaded && oldSettings.UpdateChannel != channel)
                    await CheckForUpdatesAsync();
            }
#pragma warning disable CA1031 // 设置页事件兜底，失败只记 Warning 并提示。
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "无法更改更新通道");
                if (IsLoaded && XamlRoot is { } root)
                {
                    _loadingDesktopSettings = true;
                    UpdateChannelCombo.SelectedIndex = previousChannel == "preview" ? 1 : 0;
                    _loadingDesktopSettings = false;
                    await UpdatePromptDialog.ShowMessageAsync(root, "无法更改更新通道", ex.Message);
                }
            }
#pragma warning restore CA1031
            finally
            {
                if (_updateCheckCancellation is null)
                    UpdateChannelCombo.IsEnabled = true;
            }
        }

        private async Task RefreshRollbackDataAsync()
        {
            if (!_installedApp.IsInstalled)
                return;
            var navigationVersion = _navigationVersion;
            var entries = await _rollbackData.ListAsync();
            if (navigationVersion != _navigationVersion || !IsLoaded)
                return;
            RollbackDataList.ItemsSource = entries;
            RollbackDataPanel.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void OnOpenRollbackDataClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path })
                return;
            try
            {
                var navigationVersion = _navigationVersion;
                var directory = await _rollbackData.ValidateDirectoryAsync(path);
                if (navigationVersion != _navigationVersion || !IsLoaded)
                    return;
                var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(directory);
                if (!await Windows.System.Launcher.LaunchFolderAsync(folder))
                    throw new IOException("无法打开回滚前数据文件夹。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or System.Runtime.InteropServices.COMException)
            {
                Serilog.Log.Error(ex, "无法打开回滚前数据文件夹");
                if (IsLoaded)
                    await UpdatePromptDialog.ShowMessageAsync(XamlRoot, "打开文件夹失败", ex.Message);
            }
        }

        private async void OnRestoreRollbackDataClick(object sender, RoutedEventArgs e)
        {
            if (!_installedApp.IsInstalled || sender is not Button { Tag: string path } button)
                return;
            button.IsEnabled = false;
            var navigationVersion = _navigationVersion;
            var restoreRequested = false;
            try
            {
                // 预览和写请求都读实际 SQLite 版本；文件复制在服务边界转到后台。
                var directory = await _installedApp.ValidateRestoreAsync(_rollbackData.DataDirectory, path);
                if (navigationVersion != _navigationVersion || !IsLoaded || XamlRoot is null)
                    return;
                if (!await UpdatePromptDialog.ConfirmAsync(XamlRoot, "恢复数据并重启",
                    $"将用以下数据替换当前数据库：\n{directory}\n\n"
                    + "当前数据库（含 WAL/SHM）会先另存到 Backups\\update\\AniMeido-before-restore-<时间>。\n"
                    + "回滚后在旧版本里新增的记录不会保留在恢复后的数据库中。是否恢复并立即重启 AniMeido？", "恢复并重启"))
                    return;
                if (navigationVersion != _navigationVersion || !IsLoaded || XamlRoot is null)
                    return;
                if (Application.Current is not App app)
                    throw new InvalidOperationException("应用退出入口不可用。");
                await _installedApp.RequestRestoreAsync(_rollbackData.DataDirectory, directory);
                restoreRequested = true;
                await _installedUpdates.RestartAsync(app.RequestExit);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (restoreRequested || ex is not AniMeido.Updates.Install.InstallException)
                    Serilog.Log.Error(ex, "无法请求恢复数据并重启");
                if (navigationVersion == _navigationVersion && IsLoaded && XamlRoot is { } root)
                    await UpdatePromptDialog.ShowMessageAsync(root, "无法恢复数据", ex.Message);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private async void OnDeleteRollbackDataClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } button)
                return;
            button.IsEnabled = false;
            var navigationVersion = _navigationVersion;
            try
            {
                var directory = await _rollbackData.ValidateDirectoryAsync(path);
                if (navigationVersion != _navigationVersion || !IsLoaded || XamlRoot is null)
                    return;
                if (!await UpdatePromptDialog.ConfirmAsync(XamlRoot, "删除回滚前数据",
                    $"将永久删除这份回滚前数据及其文件，不能撤销。不会删除当前数据库。\n{path}", "删除"))
                    return;
                if (navigationVersion != _navigationVersion || !IsLoaded)
                    return;
                // 审批之后重新校验目录与子树；不凭列表里的旧路径直接递归删除。
                await _rollbackData.DeleteAsync(directory);
                if (navigationVersion == _navigationVersion && IsLoaded)
                    await RefreshRollbackDataAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Serilog.Log.Error(ex, "无法删除回滚前数据：{Path}", path);
                if (IsLoaded)
                    await UpdatePromptDialog.ShowMessageAsync(XamlRoot, "删除失败", ex.Message);
            }
            finally
            {
                button.IsEnabled = true;
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
