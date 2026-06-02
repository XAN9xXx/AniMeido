using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using AniMeido.App.Services;
using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.App.Views
{
    public sealed partial class AppSettingsPage : Page
    {
        public AppSettingsPage()
        {
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

            // 初始化数据管理路径
            InitDataSettings();

            _ = UpdateCacheInfoAsync();

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
                var updateService = App.Services?.GetRequiredService<UpdateService>();
                if (updateService == null) return;
                var result = await updateService.CheckForUpdateAsync();

                ContentDialog dialog;
                if (result == null)
                {
                    dialog = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = "检查更新失败，请稍后重试或检查网络连接。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                }
                else if (result.HasUpdate)
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
                        await Windows.System.Launcher.LaunchUriAsync(new Uri(result.DownloadUrl));
                    VersionInfoText.Text = $"当前版本：v{System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version?.Major}.{System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version?.Minor}.{System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version?.Build} | 最新版本：v{result.LatestVersion}";
                    return;
                }
                else
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
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "检查更新",
                    Content = $"检查更新时发生错误：{ex.Message}",
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

        // ======== 数据管理 ========

        private void InitDataSettings()
        {
            var db = App.Services?.GetService<DatabaseService>();
            if (db == null) return;
            DbPathText.Text = db.DbPath;
            BackupPathText.Text = db.BackupDir;
            LogPathText.Text = db.LogDir;
        }

        private async Task UpdateCacheInfoAsync()
        {
            var cacheService = App.Services?.GetService<CacheService>();
            if (cacheService == null) return;

            var (dbCount, totalSizeKB) = await cacheService.GetCacheStatsAsync();
            var totalSizeMB = totalSizeKB / 1024.0;
            CacheInfoText.Text = totalSizeMB >= 1.0
                ? $"占用约 {totalSizeMB:F1} MB"
                : $"占用约 {totalSizeKB:F0} KB";
        }

        private async void OnClearCacheClick(object sender, RoutedEventArgs e)
        {
            var cacheService = App.Services?.GetService<CacheService>();
            if (cacheService == null) return;

            if (ClearCacheButton.XamlRoot is { } xamlRoot)
            {
                var dialog = new ContentDialog
                {
                    Title = "清理缓存",
                    Content = "确定要清空所有本地缓存数据吗？下次访问时将重新从网络获取。",
                    PrimaryButtonText = "确认清理",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = xamlRoot
                };
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;
            }

            ClearCacheButton.IsEnabled = false;
            ClearCacheButton.Content = "清理中…";

            try
            {
                await cacheService.ClearAllCacheAsync();
                await UpdateCacheInfoAsync();
            }
            finally
            {
                ClearCacheButton.IsEnabled = true;
                ClearCacheButton.Content = "清理缓存";
            }
        }

        private void OnOpenDbDirClick(object sender, RoutedEventArgs e)
        {
            var db = App.Services?.GetService<DatabaseService>();
            if (db?.DbPath is { } path)
                _ = Windows.System.Launcher.LaunchFolderPathAsync(System.IO.Path.GetDirectoryName(path)!);
        }

        private void OnOpenBackupDirClick(object sender, RoutedEventArgs e)
        {
            var db = App.Services?.GetService<DatabaseService>();
            if (db?.BackupDir is { } dir)
                _ = Windows.System.Launcher.LaunchFolderPathAsync(dir);
        }

        private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
        {
            var db = App.Services?.GetService<DatabaseService>();
            if (db?.LogDir is { } dir)
                _ = Windows.System.Launcher.LaunchFolderPathAsync(dir);
        }

        private async void OnBackupNowClick(object sender, RoutedEventArgs e)
        {
            var db = App.Services?.GetService<DatabaseService>();
            if (db == null) return;

            BackupNowButton.IsEnabled = false;
            BackupNowButton.Content = "备份中…";
            try
            {
                await db.BackupAsync();

                var dialog = new ContentDialog
                {
                    Title = "备份完成",
                    Content = $"数据库已备份到：\n{db.BackupDir}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "备份失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                BackupNowButton.IsEnabled = true;
                BackupNowButton.Content = "备份";
            }
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            var exportService = App.Services?.GetService<ExportService>();
            if (exportService == null) return;

            ExportButton.IsEnabled = false;
            ExportButton.Content = "导出中…";
            try
            {
                var json = await exportService.ExportAsync();

                var picker = new Windows.Storage.Pickers.FileSavePicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeChoices.Add("JSON 文件", new[] { ".json" });
                picker.SuggestedFileName = $"AniMeido-{DateTime.Now:yyyyMMdd}.json";

                if (AppServices.MainWindow is Microsoft.UI.Xaml.Window w)
                {
                    var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
                }

                var file = await picker.PickSaveFileAsync();
                if (file != null)
                    await Windows.Storage.FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "导出失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                ExportButton.IsEnabled = true;
                ExportButton.Content = "导出";
            }
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            var exportService = App.Services?.GetService<ExportService>();
            if (exportService == null) return;

            var db = App.Services?.GetService<DatabaseService>();
            if (db == null) return;

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");

            if (AppServices.MainWindow is Microsoft.UI.Xaml.Window w)
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
            }

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                var json = await Windows.Storage.FileIO.ReadTextAsync(file);

                var preview = ExportService.Preview(json);
                if (preview == null)
                {
                    var errDialog = new ContentDialog
                    {
                        Title = "导入失败",
                        Content = "文件格式无效，请选择有效的 AniMeido 导出文件。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await errDialog.ShowAsync();
                    return;
                }

                var confirmDialog = new ContentDialog
                {
                    Title = "确认导入",
                    Content = $"将导入 {preview.Tracking.Count} 条追番记录"
                             + (preview.DragZones?.Count > 0 ? $" 和 {preview.DragZones.Count} 项拖放配置" : "")
                             + "\n\n导入前会自动备份当前数据库。",
                    PrimaryButtonText = "开始导入",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };

                if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                // 导入前自动备份
                await db.BackupAsync();

                var (trackingCount, configCount, tagCount) = await exportService.ImportAsync(json);

                var doneDialog = new ContentDialog
                {
                    Title = "导入完成",
                    Content = $"成功导入 {trackingCount} 条追番记录"
                             + (configCount > 0 ? $"、{configCount} 项拖放配置" : "")
                             + (tagCount > 0 ? $"、{tagCount} 个标签绑定" : ""),
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await doneDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var errDialog = new ContentDialog
                {
                    Title = "导入失败",
                    Content = $"导入时发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await errDialog.ShowAsync();
            }
        }
    }
}
