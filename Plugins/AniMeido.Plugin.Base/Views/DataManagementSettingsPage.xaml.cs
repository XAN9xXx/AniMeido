using AniMeido.Contracts;
using AniMeido.Contracts.Desktop;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class DataManagementSettingsPage : Page
    {
        private readonly CacheService _cacheService;
        private readonly PersonalDataTransferService _transfer;
        private readonly IAppDataPaths _paths;
        private readonly IWindowHandleProvider _windowHandleProvider;
        private readonly ILogger<DataManagementSettingsPage> _logger;
        private int _visitVersion;
        private ContentDialog? _activeDialog;

        public DataManagementSettingsPage(
            CacheService cacheService,
            PersonalDataTransferService transfer,
            IAppDataPaths paths,
            IWindowHandleProvider windowHandleProvider,
            ILogger<DataManagementSettingsPage> logger)
        {
            _cacheService = cacheService;
            _transfer = transfer;
            _paths = paths;
            _windowHandleProvider = windowHandleProvider;
            _logger = logger;
            InitializeComponent();
            InitDataSettings();
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
        }

        private async void OnExportBundleClick(
            object sender,
            RoutedEventArgs e)
        {
            var visit = _visitVersion;
            if (!IsCurrentVisit(visit))
                return;

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.SuggestedStartLocation =
                Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add(
                "AniMeido 完整档案",
                [".zip"]);
            picker.SuggestedFileName =
                $"AniMeido-archive-{DateTime.Now:yyyyMMdd}";
            if (!InitializePicker(picker))
            {
                await ShowMessageAsync(
                    "无法打开选择器",
                    "主窗口句柄当前不可用，请稍后重试。",
                    visit);
                return;
            }
            var file = await picker.PickSaveFileAsync();
            if (!IsCurrentVisit(visit) || file is null)
            {
                return;
            }

            ExportBundleButton.IsEnabled = false;
            try
            {
                await _transfer.ExportBundleAsync(file.Path);
                await ShowMessageAsync(
                    "导出完成",
                    "完整档案已导出。",
                    visit);
            }
            catch (PersonalDataBusyException ex)
            {
                await ShowMessageAsync(
                    "暂时无法导出",
                    ex.Message,
                    visit);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException)
            {
                await ShowMessageAsync(
                    "导出失败",
                    ex.Message,
                    visit);
            }
            finally
            {
                ExportBundleButton.IsEnabled = true;
            }
        }

        private async void OnImportBundleClick(
            object sender,
            RoutedEventArgs e)
        {
            var visit = _visitVersion;
            if (!IsCurrentVisit(visit))
                return;

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.SuggestedStartLocation =
                Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".zip");
            if (!InitializePicker(picker))
            {
                await ShowMessageAsync(
                    "无法打开选择器",
                    "主窗口句柄当前不可用，请稍后重试。",
                    visit);
                return;
            }
            var file = await picker.PickSingleFileAsync();
            if (!IsCurrentVisit(visit) || file is null)
            {
                return;
            }

            var confirmation = new ContentDialog
            {
                Title = "导入完整档案",
                Content = "导入前会检查文件是否完整，并自动备份当前数据。",
                PrimaryButtonText = "开始导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await ShowDialogAsync(confirmation, visit);
            if (result != ContentDialogResult.Primary || !IsCurrentVisit(visit))
            {
                return;
            }

            ImportBundleButton.IsEnabled = false;
            try
            {
                var screenshotCount = await _transfer.ImportBundleAsync(
                    file.Path);
                await ShowMessageAsync(
                    "导入完成",
                    $"基础数据已导入，新增 {screenshotCount} 张截图。",
                    visit);
            }
            catch (PersonalDataBusyException ex)
            {
                await ShowMessageAsync(
                    "暂时无法导入",
                    ex.Message,
                    visit);
            }
            catch (ArchiveBundleRollbackException ex)
            {
                await ShowMessageAsync(
                    "导入失败",
                    ex.Message,
                    visit);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or Microsoft.Data.Sqlite.SqliteException)
            {
                await ShowMessageAsync(
                    "导入失败",
                    ex.Message,
                    visit);
            }
            finally
            {
                ImportBundleButton.IsEnabled = true;
            }
        }

        private bool InitializePicker(object picker)
        {
            if (!_windowHandleProvider.TryGetWindowHandle(out var handle))
            {
                return false;
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            return true;
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            var visit = ++_visitVersion;
            await UpdateCacheInfoAsync(visit);
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            _visitVersion++;
            _activeDialog?.Hide();
        }

        private bool IsCurrentVisit(int visit)
            => IsLoaded && visit == _visitVersion;

        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, int visit)
        {
            if (!IsCurrentVisit(visit) || XamlRoot is null)
                return ContentDialogResult.None;
            if (_activeDialog is not null)
                throw new InvalidOperationException("已有对话框正在显示。");

            dialog.XamlRoot = XamlRoot;
            _activeDialog = dialog;
            try
            {
                var result = await dialog.ShowAsync();
                return IsCurrentVisit(visit) ? result : ContentDialogResult.None;
            }
            finally
            {
                if (ReferenceEquals(_activeDialog, dialog))
                    _activeDialog = null;
            }
        }

        private async Task ShowMessageAsync(string title, string content, int visit)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定",
            };
            await ShowDialogAsync(dialog, visit);
        }

        private void InitDataSettings()
        {
            DbPathText.Text = _paths.DatabasePath;
            BackupPathText.Text = _paths.BackupDirectory;
            LogPathText.Text = _paths.LogDirectory;
        }

        private async Task UpdateCacheInfoAsync(int visit)
        {
            try
            {
                if (!IsCurrentVisit(visit))
                    return;
                var (dbCount, totalSizeKB) = await _cacheService.GetCacheStatsAsync();
                if (!IsCurrentVisit(visit))
                    return;
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
            var visit = _visitVersion;
            if (!IsCurrentVisit(visit) || XamlRoot is null)
                return;

            var dialog = new ContentDialog
            {
                Title = "清理缓存",
                Content = "确定要清空所有本地缓存数据吗？下次访问时将重新从网络获取。",
                PrimaryButtonText = "确认清理",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            var result = await ShowDialogAsync(dialog, visit);
            if (result != ContentDialogResult.Primary || !IsCurrentVisit(visit))
                return;

            ClearCacheButton.IsEnabled = false;
            ClearCacheButton.Content = "清理中…";

            try
            {
                await _cacheService.ClearAllCacheAsync();
                await UpdateCacheInfoAsync(visit);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or Microsoft.Data.Sqlite.SqliteException)
            {
                await ShowMessageAsync(
                    "清理失败",
                    ex.Message,
                    visit);
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
            var visit = _visitVersion;
            if (!IsCurrentVisit(visit))
                return;

            BackupNowButton.IsEnabled = false;
            BackupNowButton.Content = "备份中…";
            try
            {
                try
                {
                    await _transfer.BackupAsync();
                }
                catch (PersonalDataBusyException ex)
                {
                    await ShowMessageAsync(
                        "暂时无法备份",
                        ex.Message,
                        visit);
                    return;
                }
#pragma warning disable CA1031 // 备份与完成提示分别处理各自的最终结果。
                catch (Exception ex)
                {
                    _logger.LogError(ex, "数据库备份失败。");
                    await ShowMessageAsync(
                        "备份失败",
                        $"备份过程中发生错误：{ex.Message}",
                        visit);
                    return;
                }

                try
                {
                    await ShowMessageAsync(
                        "备份完成",
                        $"数据库已备份到：\n{_paths.BackupDirectory}",
                        visit);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "数据库备份已完成，但完成提示显示失败。");
                }
#pragma warning restore CA1031
            }
            finally
            {
                BackupNowButton.IsEnabled = true;
                BackupNowButton.Content = "备份";
            }
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            var visit = _visitVersion;
            if (!IsCurrentVisit(visit))
                return;

            ExportButton.IsEnabled = false;
            ExportButton.Content = "导出中…";
            try
            {
                var json = await _transfer.ExportJsonAsync();
                if (!IsCurrentVisit(visit))
                    return;

                var picker = new Windows.Storage.Pickers.FileSavePicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeChoices.Add("JSON 文件", new[] { ".json" });
                picker.SuggestedFileName = $"AniMeido-{DateTime.Now:yyyyMMdd}.json";

                if (!InitializePicker(picker))
                {
                    await ShowMessageAsync(
                        "无法打开选择器",
                        "主窗口句柄当前不可用，请稍后重试。",
                        visit);
                    return;
                }

                var file = await picker.PickSaveFileAsync();
                if (!IsCurrentVisit(visit) || file is null)
                    return;
                await Windows.Storage.FileIO.WriteTextAsync(file, json);
            }
            catch (PersonalDataBusyException ex)
            {
                await ShowMessageAsync(
                    "暂时无法导出",
                    ex.Message,
                    visit);
            }
            catch (HttpRequestException ex)
            {
                await ShowMessageAsync(
                    "导出失败",
                    $"导出过程中发生网络错误：{ex.Message}",
                    visit);
            }
            catch (JsonException ex)
            {
                await ShowMessageAsync(
                    "导出失败",
                    $"数据序列化失败：{ex.Message}",
                    visit);
            }
            catch (IOException ex)
            {
                await ShowMessageAsync(
                    "导出失败",
                    $"文件写入失败：{ex.Message}",
                    visit);
            }
            finally
            {
                ExportButton.IsEnabled = true;
                ExportButton.Content = "导出";
            }
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            var visit = _visitVersion;
            if (!IsCurrentVisit(visit))
                return;

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");

            if (!InitializePicker(picker))
            {
                await ShowMessageAsync(
                    "无法打开选择器",
                    "主窗口句柄当前不可用，请稍后重试。",
                    visit);
                return;
            }

            var file = await picker.PickSingleFileAsync();
            if (!IsCurrentVisit(visit) || file is null)
                return;

            ImportButton.IsEnabled = false;
            ImportButton.Content = "导入中…";

            try
            {
                var fileProps = await file.GetBasicPropertiesAsync();
                if (!IsCurrentVisit(visit))
                    return;
                if (fileProps.Size > 10 * 1024 * 1024)
                {
                    await ShowMessageAsync(
                        "文件过大",
                        "导入文件超过 10 MB 上限，请确认文件是否正确。",
                        visit);
                    return;
                }

                var json = await Windows.Storage.FileIO.ReadTextAsync(file);
                if (!IsCurrentVisit(visit))
                    return;

                var preview = await _transfer.PreviewJsonAsync(json);
                if (!IsCurrentVisit(visit))
                    return;
                if (preview == null)
                {
                    await ShowMessageAsync(
                        "导入失败",
                        "文件格式无效，请选择有效的 AniMeido 导出文件。",
                        visit);
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
                };

                var result = await ShowDialogAsync(confirmDialog, visit);
                if (result != ContentDialogResult.Primary || !IsCurrentVisit(visit))
                    return;

                var (
                    trackingCount,
                    configCount,
                    tagCount,
                    actionCenterCount) =
                    await _transfer.ImportJsonAsync(json);

                await ShowMessageAsync(
                    "导入完成",
                    $"成功导入 {trackingCount} 条追番记录"
                        + (configCount > 0 ? $"、{configCount} 项拖放配置" : "")
                        + (tagCount > 0 ? $"、{tagCount} 个标签绑定" : "")
                        + (actionCenterCount > 0
                            ? $"、{actionCenterCount} 条行动中心数据"
                            : ""),
                    visit);
            }
            catch (PersonalDataBusyException ex)
            {
                await ShowMessageAsync(
                    "暂时无法导入",
                    ex.Message,
                    visit);
            }
            catch (HttpRequestException ex)
            {
                await ShowMessageAsync(
                    "导入失败",
                    $"导入过程中发生网络错误：{ex.Message}\n\n建议检查文件格式是否正确。",
                    visit);
            }
            catch (InvalidDataException ex)
            {
                await ShowMessageAsync(
                    "导入失败",
                    ex.Message,
                    visit);
            }
            catch (JsonException ex)
            {
                await ShowMessageAsync(
                    "导入失败",
                    $"文件格式错误：{ex.Message}",
                    visit);
            }
            catch (IOException ex)
            {
                await ShowMessageAsync(
                    "导入失败",
                    $"文件读写错误：{ex.Message}",
                    visit);
            }
            catch (UnauthorizedAccessException ex)
            {
                await ShowMessageAsync(
                    "导入失败",
                    $"无权限访问文件：{ex.Message}",
                    visit);
            }
            finally
            {
                ImportButton.IsEnabled = true;
                ImportButton.Content = "导入";
            }
        }
    }
}
