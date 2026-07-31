using AniMeido.Plugin.Player.Sources;
using AniMeido.Plugin.Player.Sources.EasyBangumi;
using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.Subscriptions;
using AniMeido.Plugin.Player.Sources.Web;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AniMeido.Plugin.Player.Views;

internal sealed class SourceManagementWindow : Window
{
    private readonly OnlineSourceCatalog _sourceCatalog;
    private readonly SourcePackageInstaller _packageInstaller;
    private readonly SourceSubscriptionService _subscriptionService;
    private readonly EasyPreferenceStore _preferenceStore;
    private readonly PlayerRuntimeSettingsStore _runtimeSettings;
    private readonly WebMediaResolver _webResolver;
    private readonly Grid _root = new();
    private readonly ListView _packageList = new();
    private readonly ListView _subscriptionList = new();
    private readonly ListView _sessionList = new();
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
    private bool _sourcesChanged;

    public SourceManagementWindow(
        OnlineSourceCatalog sourceCatalog,
        SourcePackageInstaller packageInstaller,
        SourceSubscriptionService subscriptionService,
        EasyPreferenceStore preferenceStore,
        PlayerRuntimeSettingsStore runtimeSettings,
        WebMediaResolver webResolver)
    {
        _sourceCatalog = sourceCatalog;
        _packageInstaller = packageInstaller;
        _subscriptionService = subscriptionService;
        _preferenceStore = preferenceStore;
        _runtimeSettings = runtimeSettings;
        _webResolver = webResolver;
        Title = "AniMeido 播放源管理";
        Content = BuildLayout();
        ResizeWindow();
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
                "订阅、启用并诊断在线播放来源"));
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
            "声明式播放源会立即热重载；C# 代码源仍需重启 AniMeido。";
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
            Text = "源内容仅在用户确认后从上游获取；应用前会显示差异。",
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
        await Task.Yield();
        await RefreshAsync();
    }

    private async void OnPreviewNewSubscriptionClick(
        object sender,
        RoutedEventArgs e)
    {
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
        if (_subscriptionList.SelectedItem
            is SourceSubscriptionState subscription)
        {
            await PreviewAndApplyAsync(subscription.Url);
        }
    }

    private async Task PreviewAndApplyAsync(string url)
    {
        _progress.IsActive = true;
        try
        {
            var preview = await RunInBackgroundAsync(() =>
                _subscriptionService.PreviewAsync(
                    url,
                    CancellationToken.None));
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
                PrimaryButtonText = preview.ApplicableCount > 0
                    ? "应用更改"
                    : "没有更改",
                CloseButtonText = "取消",
                IsPrimaryButtonEnabled = preview.ApplicableCount > 0,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await RunInBackgroundAsync(() =>
                _subscriptionService.ApplyAsync(
                    preview,
                    CancellationToken.None));
            var reload = await ReloadSourcesAsync();
            _subscriptionUrl.Text = string.Empty;
            await RefreshAsync();
            _status.Text =
                $"已应用 {preview.ApplicableCount} 项更改；"
                + $"已热重载 {reload.CurrentCount} 个源。";
        }
#pragma warning disable CA1031 // Subscription failures are reported in the window.
        catch (Exception ex)
        {
            _status.Text = $"订阅刷新失败：{ex.Message}";
        }
#pragma warning restore CA1031
        finally
        {
            _progress.IsActive = false;
        }
    }

    private async void OnRemoveSubscriptionClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_subscriptionList.SelectedItem
            is not SourceSubscriptionState subscription)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "移除订阅",
            Content =
                "订阅关系会被删除；已导入源会保留、禁用并标记为未托管。",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunInBackgroundAsync(() =>
            _subscriptionService.RemoveAsync(
                subscription.Id,
                CancellationToken.None));
        var reload = await ReloadSourcesAsync();
        await RefreshAsync();
        _status.Text =
            "订阅已移除，相关源已保留并禁用；"
            + $"当前已加载 {reload.CurrentCount} 个源。";
    }

    private async void OnInstallPackageClick(
        object sender,
        RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(
            picker,
            WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".animeido-source");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        _progress.IsActive = true;
        try
        {
            var installed = await RunInBackgroundAsync(() =>
                _packageInstaller.InstallAsync(
                    file.Path,
                    CancellationToken.None));
            var reload = await ReloadSourcesAsync();
            await RefreshAsync();
            _status.Text =
                $"已安装 {installed}；声明式源已热重载"
                + $"（当前 {reload.CurrentCount} 个）。"
                + "若这是 C# 代码源，仍需重启。";
        }
#pragma warning disable CA1031 // Local package errors are shown in the window.
        catch (Exception ex)
        {
            _status.Text = $"源包安装失败：{ex.Message}";
        }
#pragma warning restore CA1031
        finally
        {
            _progress.IsActive = false;
        }
    }

    private async void OnTogglePackageClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_packageList.SelectedItem is not InstalledSourcePackage package
            || !package.IsValid)
        {
            return;
        }

        await RunInBackgroundAsync(() =>
            _packageInstaller.SetEnabledAsync(
                package.Id,
                !package.IsEnabled,
                CancellationToken.None));
        var reload = await ReloadSourcesAsync();
        await RefreshAsync();
        _status.Text = package.IsEnabled
            ? package.RequiresRestart
                ? $"已禁用 {package.DisplayName}；C# 代码源重启后生效。"
                : $"已禁用 {package.DisplayName}；已立即热重载。"
            : package.RequiresRestart
                ? $"已启用 {package.DisplayName}；C# 代码源重启后生效。"
                : $"已启用 {package.DisplayName}；"
                    + $"当前已加载 {reload.CurrentCount} 个源。";
    }

    private async void OnSourceSettingsClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_packageList.SelectedItem
            is not InstalledSourcePackage { IsValid: true } package)
        {
            _status.Text = "请先选择一个有效播放源。";
            return;
        }

        var isEasyBangumi = string.Equals(
            package.SourceKind,
            "easybangumi-js",
            StringComparison.Ordinal);
        var values = isEasyBangumi
            ? (await RunInBackgroundAsync(() =>
                    _preferenceStore.ReadAsync(
                        package.Id,
                        CancellationToken.None)))
                .ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var runtimeSettings = await _runtimeSettings.ReadAsync(
            CancellationToken.None);
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
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
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

        await _runtimeSettings.WriteAsync(
            runtimeSettings,
            CancellationToken.None);
        if (isEasyBangumi)
        {
            SetOrRemove(values, "Host", host.Text);
            SetOrRemove(values, "HostV2", hostV2.Text);
            SetOrRemove(values, "Timeout", timeout.Text);
            await RunInBackgroundAsync(() =>
                _preferenceStore.WriteAsync(
                    package.Id,
                    values,
                    CancellationToken.None));
        }

        _sourcesChanged = true;
        _status.Text = "源设置已保存并立即生效。";
    }

    private async void OnSaveGlobalTimeoutClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!int.TryParse(_globalTimeout.Text, out var seconds)
            || seconds is < PlayerRuntimeSettingsStore.MinimumTimeoutSeconds
                or > PlayerRuntimeSettingsStore.MaximumTimeoutSeconds)
        {
            _status.Text = "全局后台解析超时必须为 10–120 秒。";
            return;
        }

        var settings = await _runtimeSettings.ReadAsync(
            CancellationToken.None);
        settings.GlobalTimeoutSeconds = seconds;
        await _runtimeSettings.WriteAsync(settings, CancellationToken.None);
        _status.Text = "全局后台解析超时已保存并立即生效。";
    }

    private async void OnClearSelectedSessionClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_sessionList.SelectedItem is not HostWebSessionMetadata session)
        {
            _status.Text = "请先选择一个站点会话。";
            return;
        }

        if (!await ConfirmClearSessionsAsync(session.Host))
        {
            return;
        }

        await _webResolver.ClearSessionAsync(
            session.Host,
            CancellationToken.None);
        await RefreshAsync();
        _status.Text = $"已清除 {session.Host} 的登录会话。";
    }

    private async void OnClearAllSessionsClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!await ConfirmClearSessionsAsync(host: null))
        {
            return;
        }

        await _webResolver.ClearSessionAsync(
            host: null,
            CancellationToken.None);
        await RefreshAsync();
        _status.Text = "已清除全部站点登录会话。";
    }

    private async Task<bool> ConfirmClearSessionsAsync(string? host)
    {
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
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnUninstallPackageClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_packageList.SelectedItem is not InstalledSourcePackage package)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "卸载播放源",
            Content = $"确定卸载 {package.DisplayName}？",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunInBackgroundAsync(() =>
            _packageInstaller.UninstallAsync(
                package.Id,
                CancellationToken.None));
        var reload = await ReloadSourcesAsync();
        await RefreshAsync();
        _status.Text = package.RequiresRestart
            ? $"已卸载 {package.DisplayName}；C# 代码源重启后生效。"
            : $"已卸载 {package.DisplayName}；"
                + $"当前已加载 {reload.CurrentCount} 个源。";
    }

    private async Task RefreshAsync()
    {
        var result = await RunInBackgroundAsync(async () =>
        {
            var packages = _packageInstaller.ListAsync(
                CancellationToken.None);
            var subscriptions = _subscriptionService.ListAsync(
                CancellationToken.None);
            var settings = _runtimeSettings.ReadAsync(
                CancellationToken.None);
            var sessions = _webResolver.ListSessionsAsync(
                CancellationToken.None);
            return (
                Packages: await packages.ConfigureAwait(false),
                Subscriptions: await subscriptions.ConfigureAwait(false),
                Settings: await settings.ConfigureAwait(false),
                Sessions: await sessions.ConfigureAwait(false));
        });
        _packageList.ItemsSource = result.Packages;
        _subscriptionList.ItemsSource = result.Subscriptions;
        _globalTimeout.Text = result.Settings.GlobalTimeoutSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        _sessionList.ItemsSource = result.Sessions;
    }

    private static Task RunInBackgroundAsync(Func<Task> operation)
        => Task.Run(operation);

    private static Task<T> RunInBackgroundAsync<T>(
        Func<Task<T>> operation)
        => Task.Run(operation);

    private async Task<SourceCatalogReloadResult> ReloadSourcesAsync()
    {
        var result = await _sourceCatalog.ReloadAsync(
            CancellationToken.None);
        _sourcesChanged = true;
        return result;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnWindowClosed;
        if (_sourcesChanged)
        {
            SourcesChanged?.Invoke(this, EventArgs.Empty);
        }
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
        var handle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(handle);
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(840, 680));
    }
}
