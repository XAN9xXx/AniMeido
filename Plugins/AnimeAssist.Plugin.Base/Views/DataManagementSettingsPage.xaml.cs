using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class DataManagementSettingsPage : Page
    {
        private readonly CacheService _cacheService;
        private readonly ExportService? _exportService;
        private readonly BackupService _backupService;
        private readonly IAppDataPaths _paths;

        public DataManagementSettingsPage(CacheService cacheService, ExportService? exportService, BackupService backupService, IAppDataPaths paths)
        {
            _cacheService = cacheService;
            _exportService = exportService;
            _backupService = backupService;
            _paths = paths;
            InitializeComponent();
            InitDataSettings();
            _ = UpdateCacheInfoAsync();
        }

        private void InitDataSettings()
        {
            DbPathText.Text = _paths.DatabasePath;
            BackupPathText.Text = _paths.BackupDirectory;
            LogPathText.Text = _paths.LogDirectory;
        }

        private async Task UpdateCacheInfoAsync()
        {
            try
            {
                var (dbCount, totalSizeKB) = await _cacheService.GetCacheStatsAsync();
                var totalSizeMB = totalSizeKB / 1024.0;
                CacheInfoText.Text = totalSizeMB >= 1.0
                    ? $"占用约 {totalSizeMB:F1} MB"
                    : $"占用约 {totalSizeKB:F0} KB";
            }
#pragma warning disable CA1031 // 缓存信息加载失败不阻塞页面
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataManagement] UpdateCacheInfoAsync failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        private async void OnClearCacheClick(object sender, RoutedEventArgs e)
        {
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
                await _cacheService.ClearAllCacheAsync();
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
            var dir = Path.GetDirectoryName(_paths.DatabasePath);
            if (dir != null)
                _ = Windows.System.Launcher.LaunchFolderPathAsync(dir);
        }

        private void OnOpenBackupDirClick(object sender, RoutedEventArgs e)
        {
            _ = Windows.System.Launcher.LaunchFolderPathAsync(_paths.BackupDirectory);
        }

        private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
        {
            _ = Windows.System.Launcher.LaunchFolderPathAsync(_paths.LogDirectory);
        }

        private async void OnBackupNowClick(object sender, RoutedEventArgs e)
        {
            BackupNowButton.IsEnabled = false;
            BackupNowButton.Content = "备份中…";
            try
            {
                await _backupService.BackupAsync();

                var dialog = new ContentDialog
                {
                    Title = "备份完成",
                    Content = $"数据库已备份到：\n{_paths.BackupDirectory}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
#pragma warning disable CA1031 // 备份失败应显示错误提示而非崩溃
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "备份失败",
                    Content = $"备份过程中发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
#pragma warning restore CA1031
            finally
            {
                BackupNowButton.IsEnabled = true;
                BackupNowButton.Content = "备份";
            }
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            if (_exportService == null) return;

            ExportButton.IsEnabled = false;
            ExportButton.Content = "导出中…";
            try
            {
                var json = await _exportService.ExportAsync();

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
            catch (HttpRequestException ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "导出失败",
                    Content = $"导出过程中发生网络错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (JsonException ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "导出失败",
                    Content = $"数据序列化失败：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (IOException ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "导出失败",
                    Content = $"文件写入失败：{ex.Message}",
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
            if (_exportService == null) return;

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

            ImportButton.IsEnabled = false;
            ImportButton.Content = "导入中…";

            try
            {
                // 检查文件大小（上限 10 MB）
                var fileProps = await file.GetBasicPropertiesAsync();
                if (fileProps.Size > 10 * 1024 * 1024)
                {
                    var sizeDialog = new ContentDialog
                    {
                        Title = "文件过大",
                        Content = "导入文件超过 10 MB 上限，请确认文件是否正确。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await sizeDialog.ShowAsync();
                    return;
                }

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

                // 导入前使用 SQLite 在线备份确保一致性
                await _backupService.BackupAsync();

                var (
                    trackingCount,
                    configCount,
                    tagCount,
                    actionCenterCount) =
                    await _exportService.ImportAsync(json);

                var doneDialog = new ContentDialog
                {
                    Title = "导入完成",
                    Content = $"成功导入 {trackingCount} 条追番记录"
                             + (configCount > 0 ? $"、{configCount} 项拖放配置" : "")
                             + (tagCount > 0 ? $"、{tagCount} 个标签绑定" : "")
                             + (actionCenterCount > 0
                                ? $"、{actionCenterCount} 条行动中心数据"
                                : ""),
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await doneDialog.ShowAsync();
            }
            catch (HttpRequestException ex)
            {
                var errDialog = new ContentDialog
                {
                    Title = "导入失败",
                    Content = $"导入过程中发生网络错误：{ex.Message}\n\n建议检查文件格式是否正确。",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await errDialog.ShowAsync();
            }
            catch (InvalidDataException ex)
            {
                var errDialog = new ContentDialog
                {
                    Title = "导入失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await errDialog.ShowAsync();
            }
            catch (JsonException ex)
            {
                var errDialog = new ContentDialog
                {
                    Title = "导入失败",
                    Content = $"文件格式错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await errDialog.ShowAsync();
            }
            catch (IOException ex)
            {
                var errDialog = new ContentDialog
                {
                    Title = "导入失败",
                    Content = $"文件读写错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await errDialog.ShowAsync();
            }
            catch (UnauthorizedAccessException ex)
            {
                var errDialog = new ContentDialog
                {
                    Title = "导入失败",
                    Content = $"无权限访问文件：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await errDialog.ShowAsync();
            }
            finally
            {
                ImportButton.IsEnabled = true;
                ImportButton.Content = "导入";
            }
        }
    }
}
