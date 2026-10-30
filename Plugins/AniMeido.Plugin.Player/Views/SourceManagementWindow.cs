using AniMeido.Plugin.Player.Services;
using AniMeido.Plugin.Player.Sources;
using AniMeido.Plugin.Player.Sources.EasyBangumi;
using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.Subscriptions;
using AniMeido.Plugin.Player.Sources.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AniMeido.Plugin.Player.Views;

internal sealed class SourceManagementWindow : Window
{
    private const string RefreshFailureNotice = "\n更改已保存，但列表未能刷新，请重新打开管理窗口确认。";
    private const string RuntimeSettingsReadFailureNotice =
        "后台解析超时设置无法读取，本次使用默认值。原设置文件已保留，当前无法保存超时设置。";
    private readonly OnlineSourceCatalog _sourceCatalog;
    private readonly SourcePackageInstaller _packageInstaller;
    private readonly SourceSubscriptionService _subscriptionService;
    private readonly EasyPreferenceStore _preferenceStore;
    private readonly PlayerRuntimeSettingsStore _runtimeSettings;
    private readonly WebMediaResolver _webResolver;
    private readonly WebViewHostBinding _webBinding;
    private readonly ILogger _logger;
    private readonly Func<Task> _requestSessionClose;
    private readonly Grid _root = new();
    private readonly ListView _packageList = new();
    private readonly ListView _subscriptionList = new();
    private readonly ListView _sessionList = new();
    private readonly TextBlock _sessionReadNotice = new()
    {
        Text = HostWebSessionPresentation.ReadFailureNotice,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBox _subscriptionUrl = new();
    private readonly TextBox _globalTimeout = new()
    {
        Header = "全局后台解析超时（秒）",
        PlaceholderText = "30",
        MaxWidth = 240,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly TextBlock _status = new();
    private readonly ProgressRing _progress = new();
    private readonly SourceManagementSession _session = new();
    private readonly HashSet<ContentDialog> _openDialogs = [];
    private readonly AppWindow _appWindow;
    private Task? _closeTask;
    private bool IsInactive => _closed || _closeTask is not null;
    private bool _closed;

    public SourceManagementWindow(
        OnlineSourceCatalog sourceCatalog,
        SourcePackageInstaller packageInstaller,
        SourceSubscriptionService subscriptionService,
        EasyPreferenceStore preferenceStore,
        PlayerRuntimeSettingsStore runtimeSettings,
        WebMediaResolver webResolver,
        Func<Task> requestSessionClose,
        ILogger? logger = null)
    {
        _sourceCatalog = sourceCatalog;
        _packageInstaller = packageInstaller;
        _subscriptionService = subscriptionService;
        _preferenceStore = preferenceStore;
        _runtimeSettings = runtimeSettings;
        _webResolver = webResolver;
        _requestSessionClose = requestSessionClose;
        _webBinding = WebViewHostBinding.FromWindow(this);
        _logger = logger ?? NullLogger.Instance;
        Title = "AniMeido 播放源管理";
        Content = BuildLayout();
        ResizeWindow();
        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Closing += OnWindowClosing;
        Activated += OnActivated;
        Closed += OnWindowClosed;
    }

    internal event EventHandler? SourcesChanged;

    private UIElement BuildLayout()
    {
        _root.Background = PlayerVisualStyles.WindowBackground;
        _root.Padding = new Thickness(20, 18, 20, 20);
        _root.RowSpacing = 14;
        _root.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid { ColumnSpacing = 10 };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        var headingText = new StackPanel { Spacing = 3 };
        headingText.Children.Add(
            PlayerVisualStyles.CreatePageTitle("播放源管理"));
        headingText.Children.Add(
            PlayerVisualStyles.CreateSubtitle(
                "添加、启用和排查播放源"));
        heading.Children.Add(headingText);
        _progress.Width = 28;
        _progress.Height = 28;
        Grid.SetColumn(_progress, 1);
        heading.Children.Add(_progress);
        _root.Children.Add(heading);

        var tabs = new TabView
        {
            Background = PlayerVisualStyles.SurfaceBackground,
            BorderBrush = PlayerVisualStyles.SurfaceStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
        };
        tabs.TabItems.Add(CreateSubscriptionsTab());
        tabs.TabItems.Add(CreatePackagesTab());
        tabs.TabItems.Add(CreateRuntimeTab());
        Grid.SetRow(tabs, 1);
        _root.Children.Add(tabs);

        _status.Text =
            "多数播放源的改动立即生效；C# 代码源需要重启 AniMeido。";
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Opacity = 0.75;
        Grid.SetRow(_status, 2);
        _root.Children.Add(_status);
        return _root;
    }

    private TabViewItem CreateSubscriptionsTab()
    {
        _subscriptionUrl.PlaceholderText =
            "粘贴 EasyBangumi inner_source 或 ani-subs GitHub URL";
        var addButton = new Button { Content = "预览并导入" };
        PlayerVisualStyles.StyleButton(
            addButton,
            PlayerButtonTone.Primary);
        addButton.Click += OnPreviewNewSubscriptionClick;
        var refreshButton = new Button { Content = "刷新所选订阅" };
        PlayerVisualStyles.StyleButton(refreshButton);
        refreshButton.Click += OnRefreshSubscriptionClick;
        var removeButton = new Button { Content = "移除所选订阅" };
        PlayerVisualStyles.StyleButton(
            removeButton,
            PlayerButtonTone.Danger);
        removeButton.Click += OnRemoveSubscriptionClick;
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        controls.Children.Add(addButton);
        controls.Children.Add(refreshButton);
        controls.Children.Add(removeButton);
        var panel = CreateScrollableListLayout();
        var description = new TextBlock
        {
            Text = "你确认后才会从订阅地址获取内容，应用前会列出改动。",
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(description);
        var editor = new StackPanel { Spacing = 10 };
        editor.Children.Add(_subscriptionUrl);
        editor.Children.Add(controls);
        Grid.SetRow(editor, 1);
        panel.Children.Add(editor);
        Grid.SetRow(_subscriptionList, 2);
        panel.Children.Add(_subscriptionList);
        return CreateTab("订阅", panel);
    }

    private TabViewItem CreatePackagesTab()
    {
        var installButton = new Button { Content = "安装本地源包" };
        PlayerVisualStyles.StyleButton(
            installButton,
            PlayerButtonTone.Primary);
        installButton.Click += OnInstallPackageClick;
        var toggleButton = new Button { Content = "启用 / 禁用" };
        PlayerVisualStyles.StyleButton(toggleButton);
        toggleButton.Click += OnTogglePackageClick;
        var settingsButton = new Button { Content = "源设置" };
        PlayerVisualStyles.StyleButton(settingsButton);
        settingsButton.Click += OnSourceSettingsClick;
        var uninstallButton = new Button { Content = "卸载" };
        PlayerVisualStyles.StyleButton(
            uninstallButton,
            PlayerButtonTone.Danger);
        uninstallButton.Click += OnUninstallPackageClick;
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        controls.Children.Add(installButton);
        controls.Children.Add(toggleButton);
        controls.Children.Add(settingsButton);
        controls.Children.Add(uninstallButton);
        var panel = CreateScrollableListLayout();
        var description = new TextBlock
        {
            Text = "新订阅源默认禁用。建议每次只启用少量可靠来源。",
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(description);
        Grid.SetRow(controls, 1);
        panel.Children.Add(controls);
        Grid.SetRow(_packageList, 2);
        panel.Children.Add(_packageList);
        return CreateTab("已安装源", panel);
    }

    private TabViewItem CreateRuntimeTab()
    {
        var saveTimeout = new Button { Content = "保存全局超时" };
        PlayerVisualStyles.StyleButton(
            saveTimeout,
            PlayerButtonTone.Primary);
        saveTimeout.Click += OnSaveGlobalTimeoutClick;
        var clearSelected = new Button { Content = "清除所选会话" };
        PlayerVisualStyles.StyleButton(clearSelected);
        clearSelected.Click += OnClearSelectedSessionClick;
        var clearAll = new Button { Content = "清除全部会话" };
        PlayerVisualStyles.StyleButton(
            clearAll,
            PlayerButtonTone.Danger);
        clearAll.Click += OnClearAllSessionsClick;
        var sessionControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        sessionControls.Children.Add(clearSelected);
        sessionControls.Children.Add(clearAll);
        var panel = new Grid
        {
            Padding = new Thickness(12),
            RowSpacing = 10,
        };
        panel.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        var settings = new StackPanel { Spacing = 10 };
        settings.Children.Add(new TextBlock
        {
            Text =
                "后台解析默认 30 秒，可设置为 10–120 秒。"
                + "登录窗口不计入该超时。",
            TextWrapping = TextWrapping.Wrap,
        });
        settings.Children.Add(_globalTimeout);
        settings.Children.Add(saveTimeout);
        settings.Children.Add(new TextBlock
        {
            Text = "站点登录会话",
            FontSize = 18,
            Margin = new Thickness(0, 10, 0, 0),
        });
        settings.Children.Add(sessionControls);
        settings.Children.Add(_sessionReadNotice);
        panel.Children.Add(settings);
        Grid.SetRow(_sessionList, 1);
        panel.Children.Add(_sessionList);
        return CreateTab("运行时", panel);
    }

    private static Grid CreateScrollableListLayout()
    {
        var panel = new Grid
        {
            Padding = new Thickness(12),
            RowSpacing = 10,
        };
        panel.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        return panel;
    }

    private static TabViewItem CreateTab(
        string header,
        UIElement content)
        => new()
        {
            Header = header,
            IsClosable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = content,
        };

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        try
        {
            await Task.Yield();
            await RefreshAsync();
        }
#pragma warning disable CA1031 // 窗口事件最终报告列表读取失败。
        catch (Exception ex)
        {
            ReportFailure(ex, "load", "播放源列表未能读取，请重试。");
        }
#pragma warning restore CA1031
    }

    private async void OnPreviewNewSubscriptionClick(
        object sender,
        RoutedEventArgs e)
    {
        if (IsInactive)
            return;
        if (string.IsNullOrWhiteSpace(_subscriptionUrl.Text))
        {
            _status.Text = "请先粘贴订阅 URL。";
            return;
        }
        await PreviewAndApplyAsync(_subscriptionUrl.Text);
    }

    private async void OnRefreshSubscriptionClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsInactive && _subscriptionList.SelectedItem is SourceSubscriptionState subscription)
            await PreviewAndApplyAsync(subscription.Url);
    }

    private async Task PreviewAndApplyAsync(string url)
    {
        if (IsInactive)
            return;
        _progress.IsActive = true;
        var operationToken = CancellationToken.None;
        try
        {
            SourceSubscriptionPreview preview;
            using (var read = _session.TryBeginRead())
            {
                if (read is null)
                    return;
                operationToken = read.Token;
                preview = await RunInBackgroundAsync(() => _subscriptionService.PreviewAsync(url, read.Token));
            }
            operationToken = CancellationToken.None;
            if (IsInactive)
                return;

            var list = new ListView
            {
                ItemsSource = preview.Items,
                MinWidth = 600,
                MaxHeight = 440,
            };
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = $"订阅刷新预览 · {preview.Kind}",
                Content = list,
                PrimaryButtonText = preview.ApplicableCount > 0 ? "应用更改" : "没有更改",
                CloseButtonText = "取消",
                IsPrimaryButtonEnabled = preview.ApplicableCount > 0,
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
                return;

            using var write = _session.TryBeginWrite();
            if (write is null)
                return;
            operationToken = write.Token;
            var applied = await RunInBackgroundAsync(() => _subscriptionService.ApplyAsync(preview, write.Token));
            var reload = await ReloadSourcesAsync(write.Token);
            if (IsInactive)
                return;
            _subscriptionUrl.Text = string.Empty;
            var refreshed = await TryRefreshAfterWriteAsync();
            if (IsInactive)
                return;
            _status.Text = $"已应用 {applied.AppliedCount} 项，失败 {applied.FailedCount} 项；当前已加载 {reload.CurrentCount} 个源。"
                + (applied.Cancelled ? "\n已取消，之前应用的更改保留。" : string.Empty)
                + (applied.Warning is null ? string.Empty : "\n" + applied.Warning)
                + string.Concat(applied.Items.Where(item => !item.Applied)
                    .Select(item => $"\n{item.DisplayName}：{item.Error}"))
                + (refreshed ? string.Empty : RefreshFailureNotice);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // 订阅操作的最终失败在窗口报告；逐项失败仍由服务返回结果。
        catch (Exception ex)
        {
            ReportFailure(ex, "subscription", "订阅更新未能完成，请重试。");
        }
#pragma warning restore CA1031
        finally
        {
            if (!IsInactive)
                _progress.IsActive = false;
        }
    }

    private async void OnRemoveSubscriptionClick(
        object sender,
        RoutedEventArgs e)
    {
        if (IsInactive || _subscriptionList.SelectedItem is not SourceSubscriptionState subscription)
            return;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = "移除订阅",
                Content = "移除后，已导入的播放源会保留，但会被停用，也不再随订阅更新。",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
                return;
            using var write = _session.TryBeginWrite();
            if (write is null)
                return;
            await RunInBackgroundAsync(() => _subscriptionService.RemoveAsync(subscription.Id, write.Token));
            var reload = await ReloadSourcesAsync(write.Token);
            var refreshed = await TryRefreshAfterWriteAsync();
            if (!IsInactive)
                _status.Text = "订阅已移除，相关源已保留并禁用；" + $"当前已加载 {reload.CurrentCount} 个源。"
                    + (refreshed ? string.Empty : RefreshFailureNotice);
        }
#pragma warning disable CA1031 // 窗口事件最终报告移除失败。
        catch (Exception ex)
        {
            ReportFailure(ex, "remove-subscription", "订阅未能移除，请重试。");
        }
#pragma warning restore CA1031
    }

    private async void OnInstallPackageClick(
        object sender,
        RoutedEventArgs e)
    {
        if (IsInactive)
            return;
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".animeido-source");
            var file = await picker.PickSingleFileAsync();
            if (file is null || IsInactive)
                return;
            using var write = _session.TryBeginWrite();
            if (write is null)
                return;

            _progress.IsActive = true;
            var installed = await RunInBackgroundAsync(() => _packageInstaller.InstallAsync(file.Path, write.Token));
            var reload = await ReloadSourcesAsync(write.Token);
            var refreshed = await TryRefreshAfterWriteAsync();
            if (!IsInactive)
                _status.Text = $"已安装 {installed}（当前 {reload.CurrentCount} 个源）。"
                    + "C# 代码源需要重启后生效，其他已立即生效。"
                    + (refreshed ? string.Empty : RefreshFailureNotice);
        }
#pragma warning disable CA1031 // 本地安装及选择器的最终失败在窗口报告。
        catch (Exception ex)
        {
            ReportFailure(ex, "install", "源包处理未能完成，请重新打开管理窗口确认。");
        }
#pragma warning restore CA1031
        finally
        {
            if (!IsInactive)
                _progress.IsActive = false;
        }
    }

    private async void OnTogglePackageClick(
        object sender,
        RoutedEventArgs e)
    {
        if (IsInactive || _packageList.SelectedItem is not InstalledSourcePackage { IsValid: true } package)
            return;
        try
        {
            using var write = _session.TryBeginWrite();
            if (write is null)
                return;
            await RunInBackgroundAsync(() => _packageInstaller.SetEnabledAsync(package.Id, !package.IsEnabled, write.Token));
            var reload = await ReloadSourcesAsync(write.Token);
            var refreshed = await TryRefreshAfterWriteAsync();
            if (IsInactive)
                return;
            _status.Text = (package.IsEnabled
                ? package.RequiresRestart
                    ? $"已禁用 {package.DisplayName}；C# 代码源重启后生效。"
                    : $"已禁用 {package.DisplayName}；已立即生效。"
                : package.RequiresRestart
                    ? $"已启用 {package.DisplayName}；C# 代码源重启后生效。"
                    : $"已启用 {package.DisplayName}；当前已加载 {reload.CurrentCount} 个源。")
                + (refreshed ? string.Empty : RefreshFailureNotice);
        }
#pragma warning disable CA1031 // 窗口事件最终报告启停失败。
        catch (Exception ex)
        {
            ReportFailure(ex, "toggle", "播放源启停未完成，请重试。");
        }
#pragma warning restore CA1031
    }

    private async void OnSourceSettingsClick(
        object sender,
        RoutedEventArgs e)
    {
        if (IsInactive)
            return;

        if (_packageList.SelectedItem
            is not InstalledSourcePackage { IsValid: true } package)
        {
            _status.Text = "请先选择一个有效播放源。";
            return;
        }

        var stage = "read";
        var operationToken = CancellationToken.None;
        try
        {
            var isEasyBangumi = string.Equals(
                package.SourceKind,
                "easybangumi-js",
                StringComparison.Ordinal);
            Dictionary<string, string> values;
            PlayerRuntimeSettings runtimeSettings;
            using (var read = _session.TryBeginRead())
            {
                if (read is null)
                    return;

                operationToken = read.Token;
                values = isEasyBangumi
                    ? (await _preferenceStore.ReadAsync(package.Id, read.Token))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                var runtimeResult = await _runtimeSettings.ReadAsync(read.Token);
                runtimeSettings = UseRuntimeSettings(runtimeResult);
            }
            operationToken = CancellationToken.None;
            if (IsInactive)
                return;

            var resolutionTimeout = new TextBox
            {
                Header = "后台解析超时覆盖（秒）",
                PlaceholderText = "留空使用源声明或全局设置",
                Text = runtimeSettings.SourceTimeoutSeconds.TryGetValue(
                    package.Id,
                    out var seconds)
                        ? seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty,
            };
            var host = new TextBox
            {
                Header = "Host",
                Text = values.GetValueOrDefault("Host") ?? string.Empty,
            };
            var hostV2 = new TextBox
            {
                Header = "HostV2",
                Text = values.GetValueOrDefault("HostV2") ?? string.Empty,
            };
            var timeout = new TextBox
            {
                Header = "Timeout（毫秒）",
                Text = values.GetValueOrDefault("Timeout") ?? string.Empty,
            };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = "解析超时留空时使用源声明值或全局值。",
            });
            content.Children.Add(resolutionTimeout);
            if (isEasyBangumi)
            {
                content.Children.Add(host);
                content.Children.Add(hostV2);
                content.Children.Add(timeout);
            }
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = $"{package.DisplayName} 设置",
                Content = content,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || IsInactive)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(timeout.Text)
                && isEasyBangumi
                && (!int.TryParse(timeout.Text, out var timeoutValue)
                    || timeoutValue is < 5000 or > 90000))
            {
                _status.Text = "Timeout 必须留空或填写 5000–90000 毫秒。";
                return;
            }

            if (!string.IsNullOrWhiteSpace(resolutionTimeout.Text)
                && (!int.TryParse(
                        resolutionTimeout.Text,
                        out var resolutionSeconds)
                    || resolutionSeconds
                        is < PlayerRuntimeSettingsStore.MinimumTimeoutSeconds
                        or > PlayerRuntimeSettingsStore.MaximumTimeoutSeconds))
            {
                _status.Text = "后台解析超时必须留空或填写 10–120 秒。";
                return;
            }

            if (string.IsNullOrWhiteSpace(resolutionTimeout.Text))
            {
                runtimeSettings.SourceTimeoutSeconds.Remove(package.Id);
            }
            else
            {
                runtimeSettings.SourceTimeoutSeconds[package.Id] =
                    int.Parse(
                        resolutionTimeout.Text,
                        System.Globalization.CultureInfo.InvariantCulture);
            }

            if (isEasyBangumi)
            {
                SetOrRemove(values, "Host", host.Text);
                SetOrRemove(values, "HostV2", hostV2.Text);
                SetOrRemove(values, "Timeout", timeout.Text);
            }

            using var write = _session.TryBeginWrite();
            if (write is null)
                return;
            operationToken = write.Token;

            stage = "save";
            await _runtimeSettings.WriteAsync(
                runtimeSettings,
                write.Token);
            if (isEasyBangumi)
            {
                await _preferenceStore.WriteAsync(
                    package.Id,
                    values,
                    write.Token);
            }

            _session.MarkSourcesChanged();
            if (!IsInactive)
                _status.Text = "源设置已保存并立即生效。";
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // 源设置读写失败由事件最终报告，不让异常逃出窗口事件。
        catch (Exception ex)
        {
            _logger.LogError(ex, "Source settings {Stage} failed for source {SourceId}.", stage, package.Id);
            if (!IsInactive)
                _status.Text = stage == "save"
                    ? "源设置未能全部保存。请重新打开设置确认。"
                    : "源设置无法读取，请检查设置文件后重试。";
        }
#pragma warning restore CA1031
    }

    private async void OnSaveGlobalTimeoutClick(
        object sender,
        RoutedEventArgs e)
    {
        if (IsInactive)
            return;
        if (!int.TryParse(_globalTimeout.Text, out var seconds)
            || seconds is < PlayerRuntimeSettingsStore.MinimumTimeoutSeconds
                or > PlayerRuntimeSettingsStore.MaximumTimeoutSeconds)
        {
            _status.Text = "全局后台解析超时必须为 10–120 秒。";
            return;
        }
        Exception? runtimeReadFailure = null;
        try
        {
            using var write = _session.TryBeginWrite();
            if (write is null)
                return;
            var runtimeResult = await _runtimeSettings.ReadAsync(write.Token);
            runtimeReadFailure = runtimeResult.FallbackReason;
            var settings = UseRuntimeSettings(runtimeResult);
            settings.GlobalTimeoutSeconds = seconds;
            await _runtimeSettings.WriteAsync(settings, write.Token);
            if (!IsInactive)
                _status.Text = "全局后台解析超时已保存并立即生效。";
        }
#pragma warning disable CA1031 // 窗口事件最终报告运行时设置保存失败。
        catch (Exception ex)
        {
            ReportFailure(ex, "save-timeout", runtimeReadFailure is null
                ? "全局后台解析超时未能保存，请重试。"
                : "全局后台解析超时未能保存。原设置文件已保留，请检查文件后重试。");
        }
#pragma warning restore CA1031
    }

    private async void OnClearSelectedSessionClick(
        object sender,
        RoutedEventArgs e)
    {
        if (IsInactive)
            return;
        if (_sessionList.SelectedItem is not HostWebSessionMetadata session)
        {
            _status.Text = "请先选择一个站点会话。";
            return;
        }
        try
        {
            if (!await ConfirmClearSessionsAsync(session.Host))
                return;
            using var write = _session.TryBeginWrite();
            if (write is null)
                return;
            var result = await _webResolver.ClearSessionAsync(session.Host, _webBinding, write.Token);
            var refreshed = await TryRefreshAfterWriteAsync();
            ShowSessionClearResult(session.Host, result, refreshed);
        }
#pragma warning disable CA1031 // 窗口事件最终报告站点会话清理失败。
        catch (Exception ex)
        {
            ReportFailure(ex, "clear-session", "站点登录会话清理未能全部完成，请检查会话列表后重试。");
        }
#pragma warning restore CA1031
    }

    private async void OnClearAllSessionsClick(
        object sender,
        RoutedEventArgs e)
    {
        if (IsInactive)
            return;
        try
        {
            if (!await ConfirmClearSessionsAsync(host: null))
                return;
            using var write = _session.TryBeginWrite();
            if (write is null)
                return;
            var result = await _webResolver.ClearSessionAsync(host: null, _webBinding, write.Token);
            var refreshed = await TryRefreshAfterWriteAsync();
            ShowSessionClearResult(host: null, result, refreshed);
        }
#pragma warning disable CA1031 // 窗口事件最终报告站点会话清理失败。
        catch (Exception ex)
        {
            ReportFailure(ex, "clear-all-sessions", "站点登录会话未能全部清除，请重试。");
        }
#pragma warning restore CA1031
    }

    private void ShowSessionClearResult(string? host, HostWebSessionClearResult result, bool refreshed)
    {
        if (result.MetadataFailure is { } failure)
        {
            _logger.LogWarning(failure,
                "Site cookies were cleared, but session records could not be saved for {Host}.", host);
        }
        if (!IsInactive)
        {
            var refreshNotice = result.MetadataFailure is null
                ? RefreshFailureNotice
                : "\n列表未能刷新，请重新打开管理窗口确认。";
            _status.Text = HostWebSessionPresentation.GetClearResultText(host, result)
                + (refreshed ? string.Empty : refreshNotice);
        }
    }

    private async Task<bool> ConfirmClearSessionsAsync(string? host)
    {
        if (IsInactive)
            return false;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "清除登录会话",
            Content = host is null
                ? "将清除所有源站 Cookie 与会话元数据。"
                : $"将清除 {host} 的 Cookie 与会话元数据。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private async void OnUninstallPackageClick(
        object sender,
        RoutedEventArgs e)
    {
        if (IsInactive || _packageList.SelectedItem is not InstalledSourcePackage package)
            return;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = "卸载播放源",
                Content = $"确定卸载 {package.DisplayName}？",
                PrimaryButtonText = "卸载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
                return;
            using var write = _session.TryBeginWrite();
            if (write is null)
                return;
            await RunInBackgroundAsync(() => _packageInstaller.UninstallAsync(package.Id, write.Token));
            var reload = await ReloadSourcesAsync(write.Token);
            var refreshed = await TryRefreshAfterWriteAsync();
            if (!IsInactive)
                _status.Text = (package.RequiresRestart
                    ? $"已卸载 {package.DisplayName}；C# 代码源重启后生效。"
                    : $"已卸载 {package.DisplayName}；当前已加载 {reload.CurrentCount} 个源。")
                    + (refreshed ? string.Empty : RefreshFailureNotice);
        }
#pragma warning disable CA1031 // 窗口事件最终报告卸载失败。
        catch (Exception ex)
        {
            ReportFailure(ex, "uninstall", "播放源未能卸载，请重试。");
        }
#pragma warning restore CA1031
    }

    private async Task RefreshAsync()
    {
        if (IsInactive)
            return;
        using var read = _session.TryBeginRead();
        if (read is null)
            return;
        try
        {
            var result = await RunInBackgroundAsync(async () =>
            {
                var packages = _packageInstaller.ListAsync(read.Token);
                var subscriptions = _subscriptionService.ListAsync(read.Token);
                var settings = _runtimeSettings.ReadAsync(read.Token);
                var sessions = _webResolver.ListSessionsAsync(read.Token);
                await Task.WhenAll(packages, subscriptions, settings, sessions).ConfigureAwait(false);
                return (
                    Packages: await packages.ConfigureAwait(false),
                    Subscriptions: await subscriptions.ConfigureAwait(false),
                    Settings: await settings.ConfigureAwait(false),
                    Sessions: await sessions.ConfigureAwait(false));
            });
            if (IsInactive || read.Token.IsCancellationRequested)
                return;
            _packageList.ItemsSource = result.Packages;
            _subscriptionList.ItemsSource = result.Subscriptions;
            var runtimeSettings = UseRuntimeSettings(result.Settings);
            _globalTimeout.Text = runtimeSettings.GlobalTimeoutSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            _sessionList.ItemsSource = result.Sessions.Items;
            _sessionReadNotice.Visibility = result.Sessions.ReadFailure is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (result.Sessions.ReadFailure is { } failure)
            {
                _logger.LogWarning(failure,
                    "Site session records could not be read; showing the recovery notice.");
            }
        }
        catch (OperationCanceledException) when (read.Token.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TryRefreshAfterWriteAsync()
    {
        try
        {
            await RefreshAsync();
            return true;
        }
#pragma warning disable CA1031 // 更改已提交；纯列表刷新失败只报告降级，不改写保存结果。
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The operation finished, but the management list could not be refreshed.");
            return false;
        }
#pragma warning restore CA1031
    }

    private static Task RunInBackgroundAsync(Func<Task> operation)
        => Task.Run(operation);

    private static Task<T> RunInBackgroundAsync<T>(Func<Task<T>> operation)
        => Task.Run(operation);

    private async Task<SourceCatalogReloadResult> ReloadSourcesAsync(CancellationToken cancellationToken)
    {
        var result = await _sourceCatalog.ReloadAsync(cancellationToken);
        _session.MarkSourcesChanged();
        return result;
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (IsInactive)
            return ContentDialogResult.None;
        _openDialogs.Add(dialog);
        try
        {
            var result = await dialog.ShowAsync();
            return IsInactive ? ContentDialogResult.None : result;
        }
        finally
        {
            _openDialogs.Remove(dialog);
        }
    }

    private PlayerRuntimeSettings UseRuntimeSettings(PlayerRuntimeReadResult result)
    {
        if (result.FallbackReason is { } failure)
        {
            _logger.LogWarning(failure,
                "Runtime timeout settings could not be read; showing defaults and preserving the original file.");
            if (!IsInactive)
                _status.Text = RuntimeSettingsReadFailureNotice;
        }
        else if (!IsInactive && _status.Text == RuntimeSettingsReadFailureNotice)
        {
            _status.Text = string.Empty;
        }

        return result.Settings;
    }

    private void ReportFailure(Exception error, string operation, string message)
    {
        _logger.LogError(error, "Source management {Operation} failed.", operation);
        if (!IsInactive)
            _status.Text = message;
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        try
        {
            // 先让会话所有者取消播放器，窗口收尾才有机会取得共享解析器。
            await _requestSessionClose();
        }
#pragma warning disable CA1031 // 共享关闭流程已报告故障；事件只观察同一任务，不重复记录。
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    public Task CloseAsync()
    {
        if (_closeTask is not null)
            return _closeTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closeTask = completion.Task;
        _ = CompleteCloseAsync(completion);
        return completion.Task;
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try
        {
            await CloseCoreAsync();
            completion.TrySetResult();
        }
#pragma warning disable CA1031 // 原始关闭故障在共享任务所有者处只报告一次，并继续交给 Manager。
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            _logger.LogError(ex, "Source management close failed.");
            if (!_closed)
                _status.Text = "播放源管理关闭未完成，请退出播放插件后重试。";
        }
#pragma warning restore CA1031
    }

    private async Task CloseCoreAsync()
    {
        _root.IsHitTestVisible = false;
        Activated -= OnActivated;
        var stopping = _session.StopAsync();
        try
        {
            foreach (var dialog in _openDialogs.ToArray())
            {
                try
                {
                    dialog.Hide();
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    _logger.LogWarning(ex, "A source management dialog could not be hidden during close.");
                }
            }
        }
        finally
        {
            try
            {
                await stopping;
            }
#pragma warning disable CA1031 // 会话已排空；取消回调故障只在此报告一次，不重复交给 Manager。
            catch (Exception ex)
            {
                _logger.LogError(ex, "Source management cancellation failed after operations drained.");
            }
#pragma warning restore CA1031
        }
        _webResolver.InvalidateBinding(_webBinding);
        await _webResolver.DetachUiThreadAsync(_webBinding);
        _appWindow.Closing -= OnWindowClosing;
        Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _webResolver.InvalidateBinding(_webBinding);
        Closed -= OnWindowClosed;
        Activated -= OnActivated;
        if (_session.HasSourceChanges)
            SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SetOrRemove(
        IDictionary<string, string> values,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            values.Remove(key);
        }
        else
        {
            values[key] = value.Trim();
        }
    }

    private void ResizeWindow()
    {
        DpiWindowSizing.Resize(this, 840, 680);
    }
}
