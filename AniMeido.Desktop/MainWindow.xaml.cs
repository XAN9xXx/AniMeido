using AniMeido.App.Controls;
using AniMeido.App.Services;
using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AniMeido.App;

public sealed partial class MainWindow : Window
{
    private const string DefaultPageTypeName = "AniMeido.Plugin.Base.Views.TodayPage";
    private readonly PluginContributionRegistry _contributions;
    private readonly NavigationService _navigation;
    private readonly ThemeService _theme;
    private readonly StartupDialogCoordinator _dialogs;
    private readonly MainWindowDragDropAdapter _dragDrop;
    private readonly DesktopTitleBarRegions _titleBarRegions;
    private readonly StartupMigrationGate? _migration;
    private readonly string? _migrationBackupDirectory;
    private readonly CancellationTokenSource _closing = new();
    private readonly List<DesktopShellNavigationItem> _navigationButtons = [];
    private Task _startupTask = Task.CompletedTask;
    private Task _reminderStartup = Task.CompletedTask;
    private DesktopShellNavigationItem? _settingsButton;
    private Type? _selectedPage;
    private bool _started;
    private bool _isClosing;
    private bool _dropAttached;
    private bool _expanded = true;
    private LegacyInstallationService? _legacyService;
    private IReadOnlyList<LegacyInstallation> _legacyInstallations = [];

    internal TaskCompletionSource DataReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal MainWindow(PluginContributionRegistry pluginContributions, NavigationService navigationService,
        ThemeService themeService, StartupDialogCoordinator dialogs, MainWindowDragDropAdapter dragDropAdapter,
        StartupMigrationGate? migrationGate = null, string? migrationBackupDirectory = null)
    {
        _contributions = pluginContributions;
        _navigation = navigationService;
        _theme = themeService;
        _dialogs = dialogs;
        _dragDrop = dragDropAdapter;
        _migration = migrationGate;
        _migrationBackupDirectory = migrationBackupDirectory;
        InitializeComponent();
        if (AppDataRoots.IsIsolated) Title += AppDataRoots.DisplaySuffix;
        AppTitleText.Text = Title;

        // Win32 在首次显示前决定客户区，后续只调整已知标题栏与内容部件。
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(AppTitleBar);
        _titleBarRegions = new(RootGrid, BackButton, rectangles => DesktopNativeWindow.SetTitleBarPassthrough(this, rectangles));
        TitleBarHelper.SetWindowIcon(this);
        _navigation.Initialize(ContentFrame);
        _navigation.Navigated += OnNavigated;
        _contributions.Changed += OnContributionsChanged;
        _theme.ThemeChanged += OnThemeChanged;
        Activated += OnActivated;
        Closed += OnClosed;
        BuildNavigation();
        ApplyShellAppearance();
        UpdateBackButton();
    }

    private async void OnShellLoaded(object sender, RoutedEventArgs args) => await StartAsync();
    private async void OnRetryMigrationClick(object sender, RoutedEventArgs args) => await StartAsync();

    private Task StartAsync()
    {
        if (_isClosing || _started || !_startupTask.IsCompleted) return _startupTask;
        _startupTask = RunStartupAsync();
        return _startupTask;
    }

    private async Task RunStartupAsync()
    {
        try
        {
            StartupOverlay.Visibility = Visibility.Visible;
            StartupRing.IsActive = true;
            MigrationRetry.IsEnabled = false;
            if (_migration is { IsReady: false })
            {
                MigrationTitle.Text = "正在迁移资料…";
                MigrationStatus.Text = "完成后将打开资料页面。";
                MigrationStatus.Visibility = Visibility.Visible;
                if (!await _migration.TryRunAsync(_closing.Token))
                {
                    if (!_isClosing) ShowStartupFailure("资料迁移未完成", "请联网后重试。原有个人记录保留，备份可从下方打开。");
                    return;
                }
            }
            _closing.Token.ThrowIfCancellationRequested();
            _reminderStartup = InitializePlanRemindersSafelyAsync();
            DesktopUiWork.Track(_reminderStartup);
            if (!_dropAttached)
            {
                _dragDrop.Attach(RootGrid, NavigationPane, ContentFrame);
                _dropAttached = true;
            }
            DataReady.TrySetResult();
            var initial = SelectInitialPage(_contributions.NavigationItems);
            if (initial?.PageType is { } pageType) _navigation.NavigateTopLevel(pageType);
            else _navigation.NavigateTopLevel(typeof(Views.SettingPage));
            StartupRing.IsActive = false;
            StartupOverlay.Visibility = Visibility.Collapsed;
            await _dialogs.ShowStartupDialogsAsync(RootGrid, _closing.Token);
            _closing.Token.ThrowIfCancellationRequested();
            _started = true;
        }
        catch (OperationCanceledException) when (_isClosing) { }
#pragma warning disable CA1031 // 主窗口启动的最终错误边界，同时保留可重试入口。
        catch (Exception error)
        {
            Serilog.Log.Error(error, "主窗口初始化失败");
            if (!_isClosing) ShowStartupFailure("窗口加载未完成", "请重试。");
        }
#pragma warning restore CA1031
    }

    internal static PluginNavigationItem? SelectInitialPage(IReadOnlyList<PluginNavigationItem> items)
        => items.FirstOrDefault(item => item.Kind == PluginNavigationItemKind.Page && !item.IsSettingsPage
            && item.PageType is not null && item.PageTypeName == DefaultPageTypeName)
            ?? items.FirstOrDefault(item => item.Kind == PluginNavigationItemKind.Page && !item.IsSettingsPage && item.PageType is not null);

    private void ShowStartupFailure(string title, string message)
    {
        StartupOverlay.Visibility = Visibility.Visible;
        StartupRing.IsActive = false;
        MigrationTitle.Text = title;
        MigrationStatus.Text = message;
        MigrationStatus.Visibility = MigrationActions.Visibility = Visibility.Visible;
        MigrationRetry.IsEnabled = true;
    }

    private async Task InitializePlanRemindersSafelyAsync()
    {
        try { await _theme.InitializePlanRemindersAsync(_closing.Token); }
        catch (OperationCanceledException) when (_isClosing) { }
#pragma warning disable CA1031 // 提醒失败保留记录并提供重试，主窗口继续启动。
        catch (Exception error)
        {
            Serilog.Log.Error(error, "补番提醒初始化失败");
            if (!_isClosing) _theme.ReportPlanReminderFailure();
        }
#pragma warning restore CA1031
    }

    internal Task WaitForStartupAsync() => Task.WhenAll(_startupTask, _reminderStartup);

    internal void QueueFinalClose() => DesktopWindowClose.Enqueue(DispatcherQueue, Close);

    internal void BeginShutdown()
    {
        if (_isClosing) return;
        _isClosing = true;
        _closing.Cancel();
        DataReady.TrySetCanceled();
        StartupRing.IsActive = false;
        _contributions.Changed -= OnContributionsChanged;
        _navigation.Navigated -= OnNavigated;
        _theme.ThemeChanged -= OnThemeChanged;
        Activated -= OnActivated;
        _navigation.Stop();
        _titleBarRegions.Dispose();
        _dragDrop.Dispose();
        BackButton.IsEnabled = false;
        NavigationPane.IsHitTestVisible = false;
        // 在窗口仍有效时触发页面 Unloaded，让正在读取的页面立即取消。
        ContentFrame.Content = null;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        BeginShutdown();
        _closing.Dispose();
    }

    private void BuildNavigation()
    {
        NavigationItems.Children.Clear();
        SettingsItems.Children.Clear();
        _navigationButtons.Clear();
        foreach (var item in _contributions.NavigationItems.Where(item => !item.IsSettingsPage))
        {
            var button = CreateNavigationButton(item.Label, item.Icon);
            button.Tag = item;
            button.IsEnabled = item.Kind == PluginNavigationItemKind.Command || item.PageType is not null;
            _navigationButtons.Add(button);
            NavigationItems.Children.Add(button);
        }
        _settingsButton = CreateNavigationButton("设置", "\uE713");
        SettingsItems.Children.Add(_settingsButton);
        UpdateNavigationAppearance();
        SyncSelection();
    }

    private DesktopShellNavigationItem CreateNavigationButton(string label, string glyph)
    {
        var button = new DesktopShellNavigationItem(label, glyph)
        {
            Style = (Style)RootGrid.Resources["DesktopShellNavigationStyle"],
        };
        button.Click += OnNavigationClick;
        return button;
    }

    private void OnContributionsChanged(object? sender, EventArgs args)
    {
        if (_isClosing) return;
        DispatcherQueue.TryEnqueue(() => { if (!_isClosing) BuildNavigation(); });
    }

    private void OnNavigationClick(object sender, RoutedEventArgs args)
    {
        if (_isClosing || _migration is { IsReady: false }) { SyncSelection(); return; }
        var button = (DesktopShellNavigationItem)sender;
        try
        {
            if (ReferenceEquals(button, _settingsButton))
            {
                if (_navigation.CurrentPageType != typeof(Views.SettingPage))
                    _navigation.NavigateTopLevel(typeof(Views.SettingPage));
            }
            else if (button.Tag is PluginNavigationItem { Kind: PluginNavigationItemKind.Command, Command: { } command })
            {
                if (command.CanExecute(null)) command.Execute(null);
            }
            else if (button.Tag is PluginNavigationItem { PageType: { } target } && _navigation.CurrentPageType != target)
                _navigation.NavigateTopLevel(target);
        }
#pragma warning disable CA1031 // 导航入口记录页面创建失败，保持原有导航选择。
        catch (Exception error)
        {
            Serilog.Log.Error(error, "打开导航页面失败");
            DesktopFeedback.TryShow("页面未能打开，请稍后重试。", error: true);
        }
#pragma warning restore CA1031
        finally { SyncSelection(); }
    }

    private void OnNavigated(Type? pageType)
    {
        if (_isClosing) return;
        if (pageType == typeof(Views.SettingPage) || _navigationButtons.Any(button =>
                button.Tag is PluginNavigationItem { Kind: PluginNavigationItemKind.Page } item && item.PageType == pageType))
            _selectedPage = pageType;
        SyncSelection();
        UpdateBackButton();
    }

    private void SyncSelection()
    {
        foreach (var button in _navigationButtons)
            button.IsChecked = button.Tag is PluginNavigationItem { Kind: PluginNavigationItemKind.Page, PageType: not null } item
                && item.PageType == _selectedPage;
        if (_settingsButton is not null) _settingsButton.IsChecked = _selectedPage == typeof(Views.SettingPage);
    }

    private void UpdateBackButton() => BackButton.IsEnabled = !_isClosing && _navigation.CanGoBack;
    private void OnBackClick(object sender, RoutedEventArgs args)
    {
        if (!_isClosing) _navigation.GoBack();
    }

    private void OnThemeChanged(object? sender, ElementTheme theme) => ApplyShellAppearance();
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_isClosing && args.WindowActivationState != WindowActivationState.Deactivated)
            _theme.RefreshSystemMotionPreference();
    }
    private void OnShellSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var expanded = args.NewSize.Width >= 1000;
        if (_expanded != expanded)
        {
            _expanded = expanded;
            UpdateNavigationAppearance();
        }
        UpdateTitlebarHeight();
    }

    private void ApplyShellAppearance()
    {
        if (_isClosing) return;
        var visual = _theme.CurrentAppearance.VisualStyle;
        var modern = visual == DesktopVisualStyle.Fluent11;
        Workspace.CornerRadius = new(modern ? 8 : 0, 0, 0, 0);
        Workspace.BorderThickness = modern ? new(1, 1, 0, 0) : new(0);
        TitleBarHost.Background = (Brush)Application.Current.Resources[
            visual == DesktopVisualStyle.Fluent10 ? "DesktopPageBrush" : "DesktopNavigationBrush"];
        BackButton.Width = visual == DesktopVisualStyle.Fluent10 ? 48 : 40;
        BackButton.Height = visual == DesktopVisualStyle.Fluent10 ? 32 : 36;
        BackButton.Margin = new(visual == DesktopVisualStyle.Fluent10 ? 0 : 8, 0, 0, 0);
        AppTitleText.FontFamily = (FontFamily)Application.Current.Resources["DesktopUiFontFamily"];
        AppTitleText.FontSize = (double)Application.Current.Resources["DesktopControlTextSize"];
        var titlebar = AppWindow.TitleBar;
        titlebar.PreferredHeightOption = visual == DesktopVisualStyle.Fluent10 ? TitleBarHeightOption.Standard : TitleBarHeightOption.Tall;
        titlebar.ButtonBackgroundColor = titlebar.ButtonInactiveBackgroundColor = Colors.Transparent;
        TitleBarHelper.UpdateButtonColors(AppWindow, _theme.GetCurrentTheme());
        UpdateTitlebarHeight();
        UpdateNavigationAppearance();
    }

    private void UpdateTitlebarHeight()
    {
        if (_isClosing) return;
        var native = AppWindow.TitleBar.Height;
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        var fallback = _theme.CurrentAppearance.VisualStyle == DesktopVisualStyle.Fluent10 ? 32 : 48;
        RootGrid.RowDefinitions[0].Height = new(native > 0 ? native / scale : fallback);
    }

    private void UpdateNavigationAppearance()
    {
        var width = _expanded ? 196 : 60;
        ShellBody.ColumnDefinitions[0].Width = new(width);
        TitleNavigationFill.Width = width;
        var visual = _theme.CurrentAppearance.VisualStyle;
        foreach (var button in _navigationButtons) button.ApplyAppearance(visual, _expanded, Application.Current.Resources);
        _settingsButton?.ApplyAppearance(visual, _expanded, Application.Current.Resources);
    }

    internal void ShowLegacyInstallationNotice(LegacyInstallationService service)
    {
        if (_isClosing) return;
        _legacyService = service;
        _legacyInstallations = service.Detect();
        if (_legacyInstallations.Count == 0) return;
        LegacyInstallationInfoBar.Visibility = Visibility.Visible;
        LegacyInstallationInfoBar.IsOpen = true;
    }
    private void OnLegacyNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args) => sender.Visibility = Visibility.Collapsed;

    private async void OnUninstallLegacyClick(object sender, RoutedEventArgs args)
    {
        if (_isClosing || _legacyService is null) return;
        UninstallLegacyButton.IsEnabled = false;
        try
        {
            foreach (var installation in _legacyInstallations)
            {
                if (!await _legacyService.UninstallAsync(installation))
                {
                    if (!_isClosing) LegacyInstallationInfoBar.Message = "旧版卸载未完成，可在 Windows“已安装的应用”中手动卸载。";
                    return;
                }
                if (_isClosing) return;
            }
            _legacyInstallations = _legacyService.Detect();
            if (_legacyInstallations.Count == 0) LegacyInstallationInfoBar.IsOpen = false;
            else LegacyInstallationInfoBar.Message = "仍检测到旧版，请在 Windows“已安装的应用”中确认。";
        }
#pragma warning disable CA1031 // 卸载入口保留日志与可操作提示。
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "旧版 AniMeido 卸载失败");
            if (!_isClosing) LegacyInstallationInfoBar.Message = "无法卸载旧版，请在 Windows“已安装的应用”中手动卸载。";
        }
#pragma warning restore CA1031
        finally { if (!_isClosing) UninstallLegacyButton.IsEnabled = true; }
    }

    private async void OnOpenMigrationBackupsClick(object sender, RoutedEventArgs args)
    {
        if (_migrationBackupDirectory is null) return;
        try { await DesktopNativeWindow.OpenFolderAsync(_migrationBackupDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            Serilog.Log.Warning(error, "无法打开迁移备份文件夹");
            if (!_isClosing) MigrationStatus.Text = "无法打开备份文件夹，请稍后重试。";
        }
    }
}
