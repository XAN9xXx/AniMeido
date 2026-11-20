using AniMeido.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Views;

public sealed partial class AppSettingsPage
{
    internal Func<Task<DesktopSettings>> LoadUpdateChannelSettingsAsync { get; set; } = null!;
    internal Func<string, Task> SaveUpdateChannelAsync { get; set; } = null!;
    internal Func<int, CancellationToken, Task> CheckUpdateChannelAsync { get; set; } = null!;
    internal Func<CancellationToken, Task<AppUpdateCheckResult>> ReadUpdateCheckAsync { get; set; } = null!;
    private async void OnUpdateCheckClick(object sender, RoutedEventArgs args)
        => await RunOperationAsync("检查更新失败", CheckForUpdatesAsync);

    private async Task CheckForUpdatesAsync(int visit, CancellationToken token)
    {
        UpdateCheckButton.IsEnabled = false;
        UpdateChannelCombo.IsEnabled = false;
        UpdateCheckButton.Content = "检查中…";
        try
        {
            var result = await ReadUpdateCheckAsync(token);
            if (!IsCurrent(visit) || token.IsCancellationRequested) return;
            App.LatestVersion = result.LatestVersion;
            RefreshVersionText();
            var root = XamlRoot!;
            switch (result)
            {
                case AppUpdateCheckResult.Installed installed:
                    await UpdatePromptDialog.ShowAsync(installed.Result, _installedUpdates, _desktopSettings,
                        UpdatePromptTrigger.Manual, root, token);
                    break;
                case AppUpdateCheckResult.Legacy legacy:
                    await UpdatePromptDialog.ShowAsync(legacy.Result, _desktopSettings, UpdatePromptTrigger.Manual, root, token);
                    break;
            }
        }
        finally
        {
            if (IsCurrent(visit))
            {
                UpdateCheckButton.Content = "检查";
                UpdateCheckButton.IsEnabled = true;
                UpdateChannelCombo.IsEnabled = !_operationBusy;
            }
        }
    }

    private void RefreshVersionText()
    {
        var version = _installedApp.IsInstalled ? _installedApp.Version
            : typeof(AppSettingsPage).Assembly.GetName().Version?.ToString(3);
        VersionInfoText.Text = $"当前版本 v{version ?? "未知"}    最新版本 {App.LatestVersion ?? "—"}";
    }

    private int _updateChannelIndex;

    private async void OnUpdateChannelSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingDesktopSettings || !IsLoaded || !_installedApp.IsInstalled || UpdateChannelCombo.SelectedIndex < 0) return;
        if (_operationBusy)
        {
            RestoreChannelSelection(_updateChannelIndex);
            return;
        }
        var previousIndex = _updateChannelIndex;
        _updateChannelIndex = UpdateChannelCombo.SelectedIndex;
        var channel = _updateChannelIndex == 1 ? "preview" : "stable";
        await RunOperationAsync("更新通道未能更改", async (visit, token) =>
        {
            DesktopSettings? saved = null;
            try
            {
                saved = await LoadUpdateChannelSettingsAsync();
                await SaveUpdateChannelAsync(channel);
            }
            catch
            {
                if (IsCurrent(visit))
                {
                    _updateChannelIndex = saved is null ? previousIndex : saved.UpdateChannel == "preview" ? 1 : 0;
                    RestoreChannelSelection(_updateChannelIndex);
                }
                throw;
            }
            if (saved.UpdateChannel != channel && IsCurrent(visit)) await CheckUpdateChannelAsync(visit, token);
        });
    }

    private void RestoreChannelSelection(int index)
    {
        var loading = _loadingDesktopSettings;
        _loadingDesktopSettings = true;
        try { UpdateChannelCombo.SelectedIndex = index; }
        finally { _loadingDesktopSettings = loading; }
    }

    private async Task RefreshRollbackDataAsync()
    {
        if (!_installedApp.IsInstalled) return;
        var visit = _navigationVersion;
        var entries = await _rollbackData.ListAsync();
        if (!IsCurrent(visit)) return;
        RollbackDataList.ItemsSource = entries;
        RollbackDataPanel.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnOpenRollbackDataClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string path }) return;
        await RunOperationAsync("打开文件夹失败", async (visit, _) =>
        {
            var directory = await _rollbackData.ValidateDirectoryAsync(path);
            if (IsCurrent(visit)) await DesktopNativeWindow.OpenFolderAsync(directory);
        });
    }

    private async void OnDeleteRollbackDataClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string path }) return;
        await RunOperationAsync("删除回滚数据失败", async (visit, _) =>
        {
            var directory = await _rollbackData.ValidateDirectoryAsync(path);
            if (!await ConfirmAsync("删除回滚前数据", $"将永久删除这份回滚前数据及其文件：\n{directory}\n当前数据库保留。", "删除", visit)) return;
            await _rollbackData.DeleteAsync(directory);
            if (IsCurrent(visit)) await RefreshRollbackDataAsync();
        });
    }

    private async void OnRestoreRollbackDataClick(object sender, RoutedEventArgs args)
    {
        if (!_installedApp.IsInstalled || sender is not Button { Tag: string path }) return;
        await RunOperationAsync("恢复数据失败", async (visit, _) =>
        {
            var directory = await _installedApp.ValidateRestoreAsync(_rollbackData.DataDirectory, path);
            if (!await ConfirmAsync("恢复数据并重启", $"将用以下数据替换当前数据库：\n{directory}\n\n"
                + "当前数据库及 WAL/SHM 会先另存到 Backups\\update。回滚后新增的记录不会保留在恢复后的数据库中。", "恢复并重启", visit)) return;
            if (Application.Current is not App app) throw new InvalidOperationException("应用重启入口尚未就绪。");
            await _installedApp.RequestRestoreAsync(_rollbackData.DataDirectory, directory);
            try { await _installedUpdates.RestartAsync(app.RequestExit); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                Serilog.Log.Error(ex, "恢复请求已保存，但重新启动失败");
                await ShowMessageAsync("需要重新启动", "恢复请求已保存，请关闭应用后重新打开。", visit);
            }
        });
    }
}
