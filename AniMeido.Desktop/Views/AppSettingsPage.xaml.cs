using System.Text.Json;
using AniMeido.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.App.Views;

public sealed partial class AppSettingsPage : Page
{
    private readonly AppUpdateCheckService _updateChecks;
    private readonly PluginManagementService _plugins;
    private readonly DesktopSettingsStore _desktopSettings;
    private readonly ThemeService _themeService;
    private readonly InstalledUpdateService _installedUpdates;
    private readonly InstalledAppContext _installedApp;
    private readonly RollbackDataStore _rollbackData;
    private CancellationTokenSource? _visitCancellation;
    private ContentDialog? _dialog;
    private int _navigationVersion;
    private bool _loadingDesktopSettings = true;
    private bool _operationBusy;
    private Task _pendingOperation = Task.CompletedTask;
    internal Func<bool, Task>? ApplyKeepInTrayAsync { get; set; }

    public AppSettingsPage(AppUpdateCheckService updateChecks, PluginManagementService plugins,
        DesktopSettingsStore desktopSettings, ThemeService themeService,
        InstalledUpdateService installedUpdates, InstalledAppContext installedApp, RollbackDataStore rollbackData)
    {
        _updateChecks = updateChecks;
        ReadUpdateCheckAsync = token => updateChecks.CheckAsync(token);
        _plugins = plugins;
        _desktopSettings = desktopSettings;
        LoadUpdateChannelSettingsAsync = () => desktopSettings.LoadAsync();
        SaveUpdateChannelAsync = async channel => await desktopSettings.UpdateAsync(value => value with { UpdateChannel = channel });
        CheckUpdateChannelAsync = CheckForUpdatesAsync;
        _themeService = themeService;
        _installedUpdates = installedUpdates;
        _installedApp = installedApp;
        _rollbackData = rollbackData;
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        PluginList.ItemsSource = App.Plugins;
        UpdateChannelPanel.Visibility = installedApp.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        LegacyUpdateText.Visibility = installedApp.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        RefreshVersionText();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        _visitCancellation?.Dispose();
        _visitCancellation = new();
        var visit = ++_navigationVersion;
        _plugins.StatusChanged += OnPluginHostStatusChanged;
        _loadingDesktopSettings = true;
        try
        {
            await _pendingOperation;
            if (!IsCurrent(visit)) return;
            UpdateCheckButton.Content = "检查";
            UpdateCheckButton.IsEnabled = true;
            var settings = await _desktopSettings.LoadAsync();
            if (!IsCurrent(visit)) return;
            KeepInTrayToggle.IsEnabled = true;
            KeepInTrayToggle.IsOn = settings.KeepInTrayOnClose;
            _updateChannelIndex = settings.UpdateChannel == "preview" ? 1 : 0;
            UpdateChannelCombo.SelectedIndex = _updateChannelIndex;
            UpdateChannelCombo.IsEnabled = true;
            PluginCommands.IsEnabled = true;
            await RefreshInstalledPluginsAsync();
            await RefreshRollbackDataAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or PluginOperationException)
        {
            Serilog.Log.Warning(ex, "读取设置失败");
            if (IsCurrent(visit)) _themeService.ShowFeedback("设置未能读取，请稍后重新进入。", true);
        }
        finally
        {
            if (IsCurrent(visit)) _loadingDesktopSettings = false;
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs args)
    {
        ++_navigationVersion;
        _visitCancellation?.Cancel();
        _dialog?.Hide();
        _plugins.StatusChanged -= OnPluginHostStatusChanged;
    }

    private bool IsCurrent(int visit) => visit == _navigationVersion && IsLoaded && XamlRoot is not null;

    private Task RunOperationAsync(string failureTitle, Func<int, CancellationToken, Task> operation)
    {
        var task = RunOperationCoreAsync(failureTitle, operation);
        DesktopUiWork.Track(task);
        if (!task.IsCompletedSuccessfully) _pendingOperation = task;
        return task;
    }

    private async Task RunOperationCoreAsync(string failureTitle, Func<int, CancellationToken, Task> operation)
    {
        if (_operationBusy || !IsLoaded || _visitCancellation is null) return;
        _operationBusy = true;
        PluginCommands.IsEnabled = false;
        UpdateChannelCombo.IsEnabled = false;
        var visit = _navigationVersion;
        var token = _visitCancellation.Token;
        try { await operation(visit, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 页面命令的最终错误边界；已提交写入仍由服务完成。
        catch (Exception ex)
        {
            if (ex is not PluginOperationException and not AniMeido.Updates.Install.InstallException)
                Serilog.Log.Error(ex, "设置操作失败：{Operation}", failureTitle);
            if (IsCurrent(visit)) await ShowMessageAsync(failureTitle, ex.Message, visit);
        }
#pragma warning restore CA1031
        finally
        {
            _operationBusy = false;
            if (IsCurrent(visit))
            {
                PluginCommands.IsEnabled = true;
                UpdateChannelCombo.IsEnabled = true;
            }
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string accept, int visit)
    {
        if (!IsCurrent(visit)) return false;
        var dialog = new ContentDialog
        {
            Title = title, Content = message, PrimaryButtonText = accept, CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
        };
        _dialog = dialog;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary && IsCurrent(visit); }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }

    private async Task ShowMessageAsync(string title, string message, int visit)
    {
        if (!IsCurrent(visit)) return;
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "确定", XamlRoot = XamlRoot };
        _dialog = dialog;
        try { await dialog.ShowAsync(); }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }

    private async Task RefreshInstalledPluginsAsync()
    {
        var visit = _navigationVersion;
        var state = await _plugins.GetStateAsync();
        if (!IsCurrent(visit)) return;
        InstalledPluginList.ItemsSource = state.Plugins;
        NoInstalledPluginsText.Visibility = state.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginReloadInfoBar.IsOpen = state.ReloadRequired;
        PluginHostStatusText.Text = $"插件状态：{state.HostStatus}";
    }

    private void OnPluginHostStatusChanged(object? sender, EventArgs args)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded) PluginHostStatusText.Text = $"插件状态：{_plugins.HostStatus}";
        });

    private async void OnInstallPluginClick(object sender, RoutedEventArgs args)
        => await RunOperationAsync("安装插件失败", async (visit, _) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".animeido-plugin");
            var file = await picker.PickSingleFileAsync();
            if (file is null || !IsCurrent(visit)) return;
            var installed = await _plugins.InstallAsync(file.Path);
            await RefreshInstalledPluginsAsync();
            if (IsCurrent(visit))
                await ShowMessageAsync("安装完成", $"{installed.DisplayName} {installed.Version} 已安装。", visit);
            await ReloadPluginsAsync(visit);
        });

    private async void OnEnablePluginClick(object sender, RoutedEventArgs args) => await ChangePluginAsync(sender, PluginChange.Enable);
    private async void OnDisablePluginClick(object sender, RoutedEventArgs args) => await ChangePluginAsync(sender, PluginChange.Disable);
    private async void OnRollbackPluginClick(object sender, RoutedEventArgs args) => await ChangePluginAsync(sender, PluginChange.Rollback);
    private async void OnUninstallPluginClick(object sender, RoutedEventArgs args) => await ChangePluginAsync(sender, PluginChange.Uninstall);

    private async Task ChangePluginAsync(object sender, PluginChange change)
    {
        if (sender is not Button { Tag: string id }) return;
        await RunOperationAsync("插件操作失败", async (visit, _) =>
        {
            if (change == PluginChange.Uninstall && !await ConfirmAsync("卸载插件",
                "重载后删除插件文件，插件保存的数据保留。", "卸载", visit)) return;
            await _plugins.ApplyChangeAsync(id, change);
            await RefreshInstalledPluginsAsync();
            await ReloadPluginsAsync(visit);
        });
    }

    private async void OnReloadPluginsClick(object sender, RoutedEventArgs args)
        => await RunOperationAsync("重载插件失败", (visit, _) => ReloadPluginsAsync(visit, true));

    private async Task ReloadPluginsAsync(int visit, bool force = false)
    {
        if (!IsCurrent(visit)) return;
        var outcome = await _plugins.ApplyPendingAsync(async prompt =>
            IsCurrent(visit) && (!prompt.RequiresConfirmation || await ConfirmAsync("重载插件",
                prompt.HasActiveUi ? "正在运行的插件窗口和播放会关闭。现在重载？" : "现在重载可选插件？", "重载", visit)), force);
        await RefreshInstalledPluginsAsync();
        if (outcome.FailureMessage is { } failure) await ShowMessageAsync("插件重载失败", failure, visit);
    }

    private bool _traySaveBusy;

    private async void OnKeepInTrayToggled(object sender, RoutedEventArgs args)
    {
        if (_loadingDesktopSettings || !IsLoaded || _traySaveBusy) return;
        var apply = ApplyKeepInTrayAsync;
        if (apply is null && Application.Current is App app) apply = app.SetKeepInTrayOnCloseAsync;
        if (apply is null) return;
        var requested = KeepInTrayToggle.IsOn;
        var visit = _navigationVersion;
        _traySaveBusy = true;
        KeepInTrayToggle.IsEnabled = false;
        var operation = SaveAsync();
        _pendingOperation = _pendingOperation.IsCompleted ? operation : Task.WhenAll(_pendingOperation, operation);
        DesktopUiWork.Track(operation);
        await operation;

        async Task SaveAsync()
        {
            try { await apply(requested); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (!IsCurrent(visit)) return;
                _loadingDesktopSettings = true;
                KeepInTrayToggle.IsOn = !requested;
                _loadingDesktopSettings = false;
                await ShowMessageAsync("托盘设置未能更改", ex.Message, visit);
            }
            finally
            {
                _traySaveBusy = false;
                if (IsCurrent(visit)) KeepInTrayToggle.IsEnabled = true;
            }
        }
    }

    private async void OnGitHubClick(object sender, RoutedEventArgs args)
        => await OpenLinkAsync("https://github.com/XAN9xXx/AniMeido");
    private async void OnWebsiteClick(object sender, RoutedEventArgs args)
        => await OpenLinkAsync("https://animeido.com");
    private Task OpenLinkAsync(string url) => RunOperationAsync("打开链接失败", async (_, _) =>
    {
        if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(url)))
            throw new IOException("系统未能打开浏览器，请稍后重试。");
    });
}
