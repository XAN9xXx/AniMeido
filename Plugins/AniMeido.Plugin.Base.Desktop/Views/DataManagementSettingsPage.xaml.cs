using AniMeido.Contracts;
using AniMeido.Contracts.Desktop;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class DataManagementSettingsPage : Page
{
    private readonly CacheService _cache;
    private readonly PersonalDataTransferService _transfer;
    private readonly IAppDataPaths _paths;
    private readonly IWindowHandleProvider _window;
    private readonly ILogger<DataManagementSettingsPage> _logger;
    private readonly DesktopPageOperations _operations = new();
    private ContentDialog? _dialog;
    private int _visit;
    private bool _busy;
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;
    internal Task PendingOperation => _operations.Pending;
    internal Func<bool, bool, Task<string?>> SelectFileAsync { get; set; }
    internal Func<Task<(int count, double sizeKB)>> ReadCacheStatsAsync { get; set; }

    public DataManagementSettingsPage(CacheService cacheService, PersonalDataTransferService transfer,
        IAppDataPaths paths, IWindowHandleProvider windowHandleProvider, ILogger<DataManagementSettingsPage> logger)
    {
        _cache = cacheService; _transfer = transfer; _paths = paths; _window = windowHandleProvider; _logger = logger;
        SelectFileAsync = PickFileAsync;
        ReadCacheStatsAsync = cacheService.GetCacheStatsAsync;
        InitializeComponent();
        DbPathText.Text = paths.DatabasePath; BackupPathText.Text = paths.BackupDirectory; LogPathText.Text = paths.LogDirectory;
        Loaded += (_, _) => PendingActivation = ActivateAsync();
        Unloaded += (_, _) => { ++_visit; _dialog?.Hide(); };
    }

    private bool Current(int visit) => IsLoaded && visit == _visit;
    private async Task ActivateAsync()
    {
        var visit = ++_visit;
        await _operations.DrainAsync();
        if (Current(visit)) await UpdateCacheAsync(visit);
    }
    private async Task UpdateCacheAsync(int visit)
    {
        try
        {
            var (_, kb) = await ReadCacheStatsAsync();
            if (Current(visit)) CacheInfoText.Text = kb >= 1024 ? $"占用约 {kb / 1024d:F1} MB" : $"占用约 {kb:F0} KB";
        }
        catch (Exception error) when (error is SqliteException or IOException or InvalidOperationException)
        { _logger.LogWarning(error, "Cache summary failed"); if (Current(visit)) CacheInfoText.Text = "暂时无法读取缓存大小"; }
    }

    private Task RunAsync(Func<int, Task> operation)
    {
        if (_busy || !IsLoaded) return Task.CompletedTask;
        _busy = true; SetBusy(true);
        var visit = _visit;
        var task = RunCoreAsync(); _operations.Track(task); return task;
        async Task RunCoreAsync()
        {
            try { await operation(visit); }
            catch (PersonalDataBusyException error) { if (Current(visit)) Feedback(error.Message, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException
                or InvalidOperationException or InvalidDataException or ArchiveBundleRollbackException or System.Text.Json.JsonException or HttpRequestException
                or System.Runtime.InteropServices.COMException)
            {
                _logger.LogError(error, "Data management operation failed");
                if (Current(visit)) Feedback(error is ArchiveBundleRollbackException ? error.Message : "操作未完成，请检查文件后重试。", true);
            }
            finally { _busy = false; SetBusy(false); }
        }
    }
    private void SetBusy(bool busy)
    {
        foreach (var button in new[] { ClearCacheButton, BackupNowButton, ImportButton, ExportButton, ImportBundleButton, ExportBundleButton })
            button.IsEnabled = !busy;
        DataProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Feedback(string text, bool error = false)
    {
        if (DesktopFeedback.TryShow(text, error)) return;
        DataStatus.Message = text; DataStatus.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success; DataStatus.IsOpen = true;
    }
    internal async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, int visit)
    {
        if (!Current(visit) || _dialog is not null || XamlRoot is null) return ContentDialogResult.None;
        _dialog = dialog; dialog.XamlRoot = XamlRoot;
        try { var result = await dialog.ShowAsync(); return Current(visit) ? result : ContentDialogResult.None; }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }
    private Task<ContentDialogResult> ConfirmAsync(string title, string content, string primary, int visit)
        => ShowDialogAsync(new() { Title = title, Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }, visit);

    private async Task<string?> PickFileAsync(bool importing, bool bundle)
    {
        var extension = bundle ? ".zip" : ".json";
        object picker;
        if (importing)
        {
            var open = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            open.FileTypeFilter.Add(extension); picker = open;
        }
        else
        {
            var save = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"AniMeido-{DateTime.Today:yyyyMMdd}{extension}" };
            save.FileTypeChoices.Add(bundle ? "完整档案 ZIP" : "追番数据 JSON", [extension]); picker = save;
        }
        if (!_window.TryGetWindowHandle(out var handle))
        { Feedback("文件选择窗口暂时无法打开，请稍后重试。", true); return null; }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        var file = importing ? await ((FileOpenPicker)picker).PickSingleFileAsync() : await ((FileSavePicker)picker).PickSaveFileAsync();
        return file?.Path;
    }

    private async void OnClearCacheClick(object sender, RoutedEventArgs e) => await RunAsync(async visit =>
    {
        if (await ConfirmAsync("清理缓存", "清空缓存后，访问番剧时会重新获取资料。个人记录保持原样。", "清理", visit) != ContentDialogResult.Primary) return;
        await _cache.ClearAllCacheAsync();
        if (Current(visit)) { await UpdateCacheAsync(visit); if (Current(visit)) Feedback("缓存已清理。"); }
    });
    private async void OnBackupNowClick(object sender, RoutedEventArgs e) => await RunAsync(async visit =>
    {
        await _transfer.BackupAsync();
        if (!Current(visit)) return;
        // 备份已经写入磁盘，提示窗口关闭不应把备份报告成失败。
        try { Feedback("数据库已备份。"); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { _logger.LogWarning(error, "Backup succeeded but its notification could not be shown"); }
    });
    private async void OnExportClick(object sender, RoutedEventArgs e) => await ExportAsync(false);
    private async void OnExportBundleClick(object sender, RoutedEventArgs e) => await ExportAsync(true);
    private Task ExportAsync(bool bundle) => RunAsync(async visit =>
    {
        var path = await SelectFileAsync(false, bundle);
        if (path is null || !Current(visit)) return;
        if (bundle) await _transfer.ExportBundleAsync(path, CancellationToken.None);
        else { var json = await _transfer.ExportJsonAsync(); await File.WriteAllTextAsync(path, json, CancellationToken.None); }
        if (Current(visit)) Feedback("导出完成。");
    });
    private async void OnImportClick(object sender, RoutedEventArgs e) => await ImportAsync(false);
    private async void OnImportBundleClick(object sender, RoutedEventArgs e) => await ImportAsync(true);
    private Task ImportAsync(bool bundle) => RunAsync(async visit =>
    {
        var path = await SelectFileAsync(true, bundle);
        if (path is null || !Current(visit)) return;
        string? json = null;
        var summary = "导入前会检查档案，并自动备份当前数据。";
        if (!bundle)
        {
            if (new FileInfo(path).Length > 10 * 1024 * 1024) { Feedback("导入文件超过 10 MB，请确认文件是否正确。", true); return; }
            json = await File.ReadAllTextAsync(path);
            var preview = await _transfer.PreviewJsonAsync(json);
            if (!Current(visit)) return;
            if (preview is null) { Feedback("文件格式无效，请选择 AniMeido 导出的数据。", true); return; }
            summary = $"将导入 {preview.Tracking.Count} 条追番记录、{preview.DragZones?.Count ?? 0} 项拖放配置。导入前会自动备份当前数据。";
        }
        if (await ConfirmAsync(bundle ? "导入完整档案" : "确认导入", summary, "开始导入", visit) != ContentDialogResult.Primary) return;
        string result;
        if (bundle) result = $"档案已导入，新增 {await _transfer.ImportBundleAsync(path, CancellationToken.None)} 张截图。";
        else
        {
            var (tracking, config, tags, actions) = await _transfer.ImportJsonAsync(json!, CancellationToken.None);
            result = $"已导入 {tracking} 条追番记录、{config} 项拖放配置、{tags} 个标签和 {actions} 条行动记录。";
        }
        if (Current(visit)) { await UpdateCacheAsync(visit); if (Current(visit)) Feedback(result); }
    });
    private async void OnOpenDbDirClick(object sender, RoutedEventArgs e) => await OpenDirectoryAsync(Path.GetDirectoryName(_paths.DatabasePath)!);
    private async void OnOpenBackupDirClick(object sender, RoutedEventArgs e) => await OpenDirectoryAsync(_paths.BackupDirectory);
    private async void OnOpenLogDirClick(object sender, RoutedEventArgs e) => await OpenDirectoryAsync(_paths.LogDirectory);
    private Task OpenDirectoryAsync(string path) => RunAsync(async visit =>
    { await DesktopShellServices.OpenPath(path); if (Current(visit)) DataStatus.IsOpen = false; });
}
