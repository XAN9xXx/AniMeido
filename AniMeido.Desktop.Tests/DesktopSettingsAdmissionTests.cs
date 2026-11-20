using AniMeido.App.Services;
using AniMeido.App.Views;
using AniMeido.Contracts.Notifications;
using Microsoft.Extensions.DependencyInjection;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Reflection;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopSettingsAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MainWindowStillStartsWhenRemindersFailAndTheVisibleRetryCompletesCatchUp(bool supported)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var action = new ActionCenterService(fixture.Factory);
            await action.UpsertPlanAsync(91, "启动补发", AnimePlanPriority.Normal, null, 0);
            var scheduled = DateTimeOffset.Now.AddHours(-1);
            await action.AddReminderAsync(new("startup-reminder", 91, PlanReminderKind.Absolute, null, null,
                scheduled, scheduled, PlanReminderState.Pending, null, null));
            var notifications = new ReminderNotifications
            {
                Supported = supported,
                Scheduling = supported ? Task.FromException(new InvalidOperationException("合成排期失败")) : Task.CompletedTask,
            };
            using var reminders = new PlanReminderCoordinator(action, notifications, fixture.Navigation, initiallyEnabled: false);
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { ShowPlanReminders = true, PrivacyAccepted = true, AnnouncementShown = true, VisualStyle = "Paper" });
            await using var theme = new ThemeService(store, reminders);
            await theme.InitializeThemeAsync(root);
            using var provider = new ServiceCollection().BuildServiceProvider();
            var navigation = new NavigationService(new PageFactory(provider), Microsoft.Extensions.Logging.Abstractions.NullLogger<NavigationService>.Instance);
            var registry = new PluginContributionRegistry();
            registry.SetBuiltInItems([AniMeido.Contracts.PluginNavigationItem.CreatePage("合成首页", "", typeof(DesktopBackNavigationTests.SearchStatePage).FullName!, typeof(DesktopBackNavigationTests.SearchStatePage))]);
            var drag = new DragDropService(new TrackingService(fixture.Factory), new PersonalFeatureStateStore(fixture.Factory),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DragDropService>.Instance);
            var window = new AniMeido.App.MainWindow(registry, navigation, theme, new StartupDialogCoordinator(new PrivacyService(store), store), new MainWindowDragDropAdapter(drag));
            var shell = Assert.IsType<Grid>(window.Content);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Loaded += (_, _) => loaded.TrySetResult();
            root.Children.Add(shell);
            try
            {
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await window.WaitForStartupAsync();
                Assert.True(window.DataReady.Task.IsCompletedSuccessfully);
                Assert.IsType<DesktopBackNavigationTests.SearchStatePage>(((Frame)shell.FindName("ContentFrame")).Content);
                Assert.Equal(Visibility.Collapsed, ((Grid)shell.FindName("StartupOverlay")).Visibility);
                var message = Find<TextBlock>(root, text => text.Text == "补番提醒尚未完成，请重试。");
                var toast = Assert.IsType<Grid>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(message));
                Assert.Null(Assert.Single(await action.GetRemindersAsync()).CatchUpSentAt);
                notifications.Supported = true;
                notifications.Scheduling = Task.CompletedTask;
                Invoke(toast.Children.OfType<Button>().Single(button => button.Content as string == "重试"));
                await DesktopUiWork.DrainAsync();
                Assert.False(message.IsLoaded);
                Assert.NotNull(Assert.Single(await action.GetRemindersAsync()).CatchUpSentAt);
                Assert.IsType<DesktopBackNavigationTests.SearchStatePage>(((Frame)shell.FindName("ContentFrame")).Content);
            }
            finally { window.BeginShutdown(); await window.WaitForStartupAsync(); root.Children.Remove(shell); window.Close(); await DesktopUiWork.DrainAsync(); }
        });

    [Fact]
    public Task DesktopPluginStartsWithReminderSchedulingDisabled()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var services = new ServiceCollection();
            await new AniMeido.Plugin.Base.BasePlugin().InitializeAsync(services);
            services.AddSingleton(new ActionCenterService(fixture.Factory));
            services.AddSingleton<IAppNotificationService>(new ReminderNotifications());
            services.AddSingleton<AniMeido.Contracts.IPluginNavigator>(fixture.Navigation);
            using var provider = services.BuildServiceProvider();
            Assert.False(provider.GetRequiredService<PlanReminderCoordinator>().NotificationsAvailable);
        });

    [Fact]
    public Task ReminderSettingWaitsForSchedulingAndShutdownDrainIncludesRapidDisable()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var action = new ActionCenterService(fixture.Factory);
            await action.UpsertPlanAsync(90, "待提醒作品", AnimePlanPriority.Normal, null, 0);
            await action.AddReminderAsync(new("pending", 90, PlanReminderKind.Absolute, null, null,
                DateTimeOffset.Now.AddDays(1), DateTimeOffset.Now.AddDays(1), PlanReminderState.Pending, null, null));
            var notifications = new ReminderNotifications();
            using var reminders = new PlanReminderCoordinator(action, notifications, fixture.Navigation, initiallyEnabled: false);
            var store = new DesktopSettingsStore(fixture);
            await using var theme = new ThemeService(store, reminders);
            await theme.InitializeThemeAsync(root);
            await theme.InitializePlanRemindersAsync();
            Assert.Contains("anime-plan-90", notifications.Cancelled);
            var page = TrayPage(store, theme);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            notifications.Scheduling = release.Task;
            try
            {
                await AttachAsync(root, page);
                var toggle = (ToggleSwitch)page.FindName("PlanRemindersToggle");
                Assert.False(toggle.IsOn);
                toggle.IsOn = true;
                await UntilAsync(() => notifications.ScheduleEntered);
                toggle.IsOn = false;
                await DetachAsync(root, page);
                var drain = DesktopUiWork.DrainAsync();
                Assert.False(drain.IsCompleted);
                release.SetResult();
                await drain;
                Assert.False((await store.LoadAsync()).ShowPlanReminders);
                Assert.False(reminders.NotificationsAvailable);
                Assert.Empty(notifications.Scheduled);
                Assert.Equal(PlanReminderState.Pending, Assert.Single(await action.GetRemindersAsync()).State);
            }
            finally { release.TrySetResult(); await DetachAsync(root, page); await DesktopUiWork.DrainAsync(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StartupAppliesTheSavedReminderChoiceBeforeAnyPageLoads(bool enabled)
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var action = new ActionCenterService(fixture.Factory);
            var notifications = new ReminderNotifications();
            using var reminders = new PlanReminderCoordinator(action, notifications, fixture.Navigation, initiallyEnabled: false);
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { ShowPlanReminders = enabled });
            await using var theme = new ThemeService(store, reminders);
            await theme.InitializePlanRemindersAsync();
            Assert.Equal(enabled, reminders.NotificationsAvailable);
            Assert.Equal(enabled, (await store.LoadAsync()).ShowPlanReminders);
        });

    private sealed class ReminderNotifications : IAppNotificationService
    {
        internal bool Supported { get; set; } = true;
        public bool IsSupported => Supported;
        public bool NotificationsEnabled => true;
        internal List<string> Cancelled { get; } = [];
        internal HashSet<string> Scheduled { get; } = [];
        internal bool ScheduleEntered { get; private set; }
        internal Task Scheduling { get; set; } = Task.CompletedTask;
        public async Task ScheduleAsync(AppNotificationRequest request, CancellationToken cancellationToken = default)
        {
            if (!IsSupported || !NotificationsEnabled) throw new InvalidOperationException("系统通知不可用");
            ScheduleEntered = true; await Scheduling; Scheduled.Add(request.Tag);
        }
        public Task CancelAsync(string group, string tag, CancellationToken cancellationToken = default)
        { Scheduled.Remove(tag); return Task.CompletedTask; }
        public Task CancelGroupAsync(string group, CancellationToken cancellationToken = default)
        { Cancelled.Add(group); Scheduled.Clear(); return Task.CompletedTask; }
        public IDisposable RegisterActivationHandler(string category, Func<AppNotificationActivation, CancellationToken, Task> handler) => new Registration();
        public Task OpenNotificationSettingsAsync() => Task.CompletedTask;
        private sealed class Registration : IDisposable { public void Dispose() { } }
    }

    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10)]
    [InlineData(DesktopVisualStyle.Fluent11)]
    [InlineData(DesktopVisualStyle.Paper)]
    public Task AppearanceSelectionMatchesTheRootAndPaletteBeforeAndAfterMounting(DesktopVisualStyle visual)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = visual.ToString(), ThemeSetting = "Dark", FontStyle = "Serif" });
            var shell = new Grid();
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(shell);
            Assert.Equal(DesktopAppearancePalette.For(visual, true).Page,
                "#" + ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["DesktopPageBrush"]).Color.ToString().Substring(3));
            var page = TrayPage(store, theme);
            shell.Children.Add(page);
            root.Children.Add(shell);
            await Task.Delay(30);
            root.UpdateLayout();
            var choice = (AniMeido.Plugin.Base.Views.Controls.DesktopChoice)page.FindName("ThemeCombo");
            try
            {
                foreach (var index in new[] { 1, 0, 1, 0, 1 })
                {
                    var picker = Find<Microsoft.UI.Xaml.Controls.Primitives.Selector>(choice, item => item.Visibility == Visibility.Visible);
                    picker.SelectedIndex = index;
                    await Task.Delay(30);
                    await DesktopUiWork.DrainAsync();
                    var requested = index == 1 ? ElementTheme.Dark : ElementTheme.Light;
                    Assert.Equal(requested, theme.GetCurrentTheme());
                    Assert.Equal(requested, shell.ActualTheme);
                    Assert.Equal(index, choice.SelectedIndex);
                    Assert.Equal(DesktopAppearancePalette.For(visual, index == 1).Page,
                        "#" + ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["DesktopPageBrush"]).Color.ToString().Substring(3));
                    Assert.Equal(requested.ToString(), (await store.LoadAsync()).ThemeSetting);
                }
                foreach (var index in new[] { 0, 1, 2 })
                {
                    var fontChoice = (AniMeido.Plugin.Base.Views.Controls.DesktopChoice)page.FindName("FontStyleChoice");
                    Find<Microsoft.UI.Xaml.Controls.Primitives.Selector>(fontChoice, item => item.Visibility == Visibility.Visible).SelectedIndex = index;
                    await Task.Delay(30); await DesktopUiWork.DrainAsync();
                    var expected = DesktopAppearanceFont.FromChoiceIndex(index);
                    Assert.Equal(expected, theme.CurrentAppearance.FontStyle);
                    Assert.Equal(DesktopAppearanceFont.ForStyle(expected).Family,
                        ((Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["DesktopUiFontFamily"]).Source);
                    Assert.Equal(expected.ToString(), (await store.LoadAsync()).FontStyle);
                }
                foreach (var style in new[] { DesktopVisualStyle.Fluent10, DesktopVisualStyle.Paper, DesktopVisualStyle.Fluent11 })
                {
                    var visualChoice = (AniMeido.Plugin.Base.Views.Controls.DesktopChoice)page.FindName("VisualStyleChoice");
                    Find<Microsoft.UI.Xaml.Controls.Primitives.Selector>(visualChoice, item => item.Visibility == Visibility.Visible).SelectedIndex = (int)style;
                    await Task.Delay(30); await DesktopUiWork.DrainAsync();
                    Assert.Equal(style, theme.CurrentAppearance.VisualStyle);
                    Assert.Equal(style.ToString(), (await store.LoadAsync()).VisualStyle);
                    Assert.Equal((int)style, visualChoice.SelectedIndex);
                }
            }
            finally { root.Children.Remove(shell); await DesktopUiWork.DrainAsync(); }
        });

    [Fact]
    public Task TraySaveIsIncludedInShutdownDrain()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await using var theme = new ThemeService(store);
            var page = TrayPage(store, theme);
            var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0; page.ApplyKeepInTrayAsync = async requested =>
            { calls++; await save.Task; await store.UpdateAsync(settings => settings with { KeepInTrayOnClose = requested }); };
            try
            {
                await AttachAsync(root, page); Ready(page);
                Call(page, "OnKeepInTrayToggled");
                Assert.Equal(1, calls);
                await DetachAsync(root, page);
                var draining = PluginStartup.StopAfterDataDrainAsync([], fixture.Factory); Assert.False(draining.IsCompleted);
                save.SetResult(); await draining;
            }
            finally { save.TrySetResult(); await DetachAsync(root, page); await DesktopUiWork.DrainAsync(); }
        });

    [Fact]
    public Task OldTrayFailureDoesNotChangeTheReturnedPageAndRetryWorks()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await using var theme = new ThemeService(store);
            var page = TrayPage(store, theme);
            var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0; page.ApplyKeepInTrayAsync = _ => { calls++; return calls == 1 ? save.Task : Task.CompletedTask; };
            try
            {
                await AttachAsync(root, page); Ready(page); Call(page, "OnKeepInTrayToggled");
                Assert.Equal(1, calls);
                await DetachAsync(root, page); await AttachAsync(root, page); Ready(page);
                var toggle = (ToggleSwitch)page.FindName("KeepInTrayToggle");
                save.SetException(new IOException("合成保存失败")); await DesktopUiWork.DrainAsync(); await Task.Delay(20);
                Assert.False(toggle.IsOn); Assert.Null(Field(page, "_dialog"));
                Assert.True(toggle.IsEnabled);
                Call(page, "OnKeepInTrayToggled"); await DesktopUiWork.DrainAsync(); Assert.Equal(2, calls);
            }
            finally { save.TrySetResult(); (Field(page, "_dialog") as ContentDialog)?.Hide(); await DetachAsync(root, page); await DesktopUiWork.DrainAsync(); }
        });

    [Fact]
    public Task SlowScreenshotReadAdmitsOneEditorAndNoLateSecondPopup()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync(); var page = fixture.Archives(new Source());
            var read = new TaskCompletionSource<ScreenshotSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new TaskCompletionSource<ScreenshotSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            page.ReadScreenshotSettingsAsync = _ => ++calls == 1 ? read.Task : late.Task;
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                Call(page, "OnScreenshotSettingsClick"); Call(page, "OnScreenshotSettingsClick");
                Assert.Equal(1, calls);
                read.SetResult(Settings(fixture)); await DialogAsync(page);
                ((ContentDialog)Field(page, "_dialog")!).Hide(); await page.PendingRead;
                late.SetResult(Settings(fixture)); await Task.Delay(20);
                Assert.Null(Field(page, "_dialog")); Assert.Equal(1, calls);
                Call(page, "OnScreenshotSettingsClick"); await DialogAsync(page); Assert.Equal(2, calls);
            }
            finally { read.TrySetResult(Settings(fixture)); late.TrySetResult(Settings(fixture)); await DetachAsync(root, page); await page.PendingRead; }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ScreenshotReadGateRecoversAfterLeavingOrFailure(bool fail)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync(); var page = fixture.Archives(new Source());
            var read = new TaskCompletionSource<ScreenshotSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            page.ReadScreenshotSettingsAsync = _ => ++calls == 1 ? read.Task : Task.FromResult(Settings(fixture));
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                Call(page, "OnScreenshotSettingsClick");
                if (fail) read.SetException(new IOException("合成读取失败"));
                else { await DetachAsync(root, page); read.SetResult(Settings(fixture)); }
                await page.PendingRead; Assert.Null(Field(page, "_dialog"));
                if (!fail) { await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead; }
                Call(page, "OnScreenshotSettingsClick"); await DialogAsync(page); Assert.Equal(2, calls);
            }
            finally { read.TrySetResult(Settings(fixture)); await DetachAsync(root, page); await page.PendingRead; }
        });

    [Fact]
    public Task ScreenshotGateStaysHeldUntilTheAcceptedSaveFinishes()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync(); var page = fixture.Archives(new Source());
            var calls = 0;
            page.ReadScreenshotSettingsAsync = _ => { calls++; return Task.FromResult(Settings(fixture)); };
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                Call(page, "OnScreenshotSettingsClick"); await DialogAsync(page);
                var dialog = (ContentDialog)Field(page, "_dialog")!;
                using (var blocker = await LockDatabaseAsync(fixture))
                {
                    Invoke(Find<Button>(dialog, b => b.Name == "PrimaryButton")); await Task.Delay(20);
                    Assert.False(page.PendingOperation.IsCompleted);
                    dialog.Hide(); await Task.Delay(20);
                    Call(page, "OnScreenshotSettingsClick"); Assert.Equal(1, calls);
                }
                await page.PendingOperation; await page.PendingRead;
                Call(page, "OnScreenshotSettingsClick"); await DialogAsync(page); Assert.Equal(2, calls);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; await page.PendingRead; }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ChannelIsDisabledWhileReadingOrSavingAndRepeatedSelectionIsRestored(bool pauseSave)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture); await using var theme = new ThemeService(store);
            var page = TrayPage(store, theme, Installed(fixture));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0; var saves = 0;
            page.LoadUpdateChannelSettingsAsync = async () => { reads++; if (!pauseSave) await gate.Task; return new DesktopSettings(); };
            page.SaveUpdateChannelAsync = async channel => { saves++; if (pauseSave) await gate.Task; await store.UpdateAsync(s => s with { UpdateChannel = channel }); };
            page.CheckUpdateChannelAsync = (_, _) => Task.CompletedTask;
            try
            {
                await AttachAsync(root, page); await Task.Delay(20); Ready(page);
                var combo = (AniMeido.Plugin.Base.Views.Controls.DesktopChoice)page.FindName("UpdateChannelCombo"); SelectChannel(page, 1);
                await UntilAsync(() => reads == 1);
                Assert.False(combo.IsEnabled);
                SelectChannel(page, 0); await Task.Delay(20);
                Assert.Equal(1, combo.SelectedIndex);
                gate.SetResult(); await DesktopUiWork.DrainAsync();
                Assert.Equal("preview", (await store.LoadAsync()).UpdateChannel); Assert.Equal(1, saves); Assert.True(combo.IsEnabled);
            }
            finally { gate.TrySetResult(); (Field(page, "_dialog") as ContentDialog)?.Hide(); await DetachAsync(root, page); await DesktopUiWork.DrainAsync(); }
        });

    [Fact]
    public Task AnotherBusyCommandDisablesTheChannelAndRejectsItsVisualChange()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync(); var store = new DesktopSettingsStore(fixture);
            await using var theme = new ThemeService(store); var page = TrayPage(store, theme, Installed(fixture));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await AttachAsync(root, page); await Task.Delay(20); Ready(page);
                var combo = (AniMeido.Plugin.Base.Views.Controls.DesktopChoice)page.FindName("UpdateChannelCombo");
                var task = (Task)typeof(AppSettingsPage).GetMethod("RunOperationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(page, ["合成命令", (Func<int, CancellationToken, Task>)((_, _) => gate.Task)])!;
                Assert.False(combo.IsEnabled); SelectChannel(page, 1); await Task.Delay(20);
                Assert.Equal(0, combo.SelectedIndex);
                gate.SetResult(); await task; Assert.True(combo.IsEnabled);
            }
            finally { gate.TrySetResult(); await DetachAsync(root, page); await DesktopUiWork.DrainAsync(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ChannelFailureRollsBackOnlyItsOwnVisitAndAllowsRetry(bool leave)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync(); var store = new DesktopSettingsStore(fixture);
            await using var theme = new ThemeService(store); var page = TrayPage(store, theme, Installed(fixture));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saves = 0;
            page.LoadUpdateChannelSettingsAsync = () => Task.FromResult(new DesktopSettings());
            page.SaveUpdateChannelAsync = async channel => { if (++saves == 1) await gate.Task; await store.UpdateAsync(s => s with { UpdateChannel = channel }); };
            page.CheckUpdateChannelAsync = (_, _) => Task.CompletedTask;
            try
            {
                await AttachAsync(root, page); await Task.Delay(20); Ready(page);
                var combo = (AniMeido.Plugin.Base.Views.Controls.DesktopChoice)page.FindName("UpdateChannelCombo"); SelectChannel(page, 1); await UntilAsync(() => saves == 1);
                if (leave) { await DetachAsync(root, page); await AttachAsync(root, page); Ready(page); }
                gate.SetException(new IOException("合成保存失败"));
                if (!leave) { await DialogAsync(page); Assert.Equal(0, combo.SelectedIndex); ((ContentDialog)Field(page, "_dialog")!).Hide(); }
                await DesktopUiWork.DrainAsync();
                if (leave) { Assert.Equal(1, combo.SelectedIndex); Assert.Null(Field(page, "_dialog")); }
                combo.IsEnabled = true;
                SelectChannel(page, leave ? 0 : 1); await UntilAsync(() => saves == 2); await DesktopUiWork.DrainAsync();
                Assert.True(combo.IsEnabled);
            }
            finally { gate.TrySetResult(); (Field(page, "_dialog") as ContentDialog)?.Hide(); await DetachAsync(root, page); await DesktopUiWork.DrainAsync(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ScreenshotEditorsShareAdmissionAcrossSlowReads(bool settingsFirst)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync(); var page = fixture.Archives(new Source());
            await SeedShotAsync(fixture); await page.OnNavigatedToAsync("audit-shot");
            var targets = new TaskCompletionSource<IReadOnlyList<LocalAnimeTarget>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var settings = new TaskCompletionSource<ScreenshotSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            var targetCalls = 0; var settingCalls = 0;
            page.ReadScreenshotTargetsAsync = _ => { targetCalls++; return targets.Task; };
            page.ReadScreenshotSettingsAsync = _ => { settingCalls++; return settings.Task; };
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                var first = settingsFirst ? "OnScreenshotSettingsClick" : "OnEditScreenshotClick";
                var second = settingsFirst ? "OnEditScreenshotClick" : "OnScreenshotSettingsClick";
                Call(page, first); Call(page, first); Call(page, second);
                Assert.Equal(settingsFirst ? 0 : 1, targetCalls); Assert.Equal(settingsFirst ? 1 : 0, settingCalls);
                if (settingsFirst) settings.SetResult(Settings(fixture)); else targets.SetResult([]);
                await DialogAsync(page); ((ContentDialog)Field(page, "_dialog")!).Hide(); await page.PendingRead;
                targets.TrySetResult([]); settings.TrySetResult(Settings(fixture)); await Task.Delay(20);
                Assert.Null(Field(page, "_dialog"));
            }
            finally { targets.TrySetResult([]); settings.TrySetResult(Settings(fixture)); await DetachAsync(root, page); await page.PendingRead; }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EditScreenshotReadRecoversAfterLeavingOrFailure(bool fail)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync(); var page = fixture.Archives(new Source());
            await SeedShotAsync(fixture); await page.OnNavigatedToAsync("audit-shot");
            var read = new TaskCompletionSource<IReadOnlyList<LocalAnimeTarget>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0; page.ReadScreenshotTargetsAsync = _ => ++calls == 1 ? read.Task : Task.FromResult<IReadOnlyList<LocalAnimeTarget>>([]);
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                Call(page, "OnEditScreenshotClick");
                if (fail) read.SetException(new IOException("合成目标读取失败"));
                else { await DetachAsync(root, page); read.SetResult([]); }
                await page.PendingRead; Assert.Null(Field(page, "_dialog"));
                if (!fail) { await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead; }
                Call(page, "OnEditScreenshotClick"); await DialogAsync(page); Assert.Equal(2, calls);
            }
            finally { read.TrySetResult([]); await DetachAsync(root, page); await page.PendingRead; }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DataSuccessFeedbackStaysWithItsVisitAfterSlowCacheStats(bool import)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync(); var page = fixture.DataSettings();
            var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "audit-import.json");
            await File.WriteAllTextAsync(path, await new ExportService(fixture.Factory).ExportAsync());
            page.SelectFileAsync = (_, _) => Task.FromResult<string?>(path);
            var stats = new TaskCompletionSource<(int, double)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0; var feedback = 0;
            void Feedback(string message, bool error) { if (!error) feedback++; }
            try
            {
                await AttachAsync(root, page); await page.PendingActivation;
                page.ReadCacheStatsAsync = () => { calls++; return stats.Task; };
                DesktopFeedback.Requested += Feedback;
                Invoke((Button)page.FindName(import ? "ImportButton" : "ClearCacheButton")); await DialogAsync(page);
                Invoke(Find<Button>((ContentDialog)Field(page, "_dialog")!, b => b.Name == "PrimaryButton"));
                await UntilAsync(() => calls == 1);
                if (import) Assert.Single(Directory.GetFiles(fixture.BackupDirectory, "*.db"));
                else Assert.Equal(0, (await new CacheService(fixture.Factory).GetCacheStatsAsync()).count);
                await DetachAsync(root, page); await AttachAsync(root, page);
                stats.SetResult((0, 0)); await page.PendingOperation; await page.PendingActivation;
                Assert.Equal(0, feedback); Assert.False(((InfoBar)page.FindName("DataStatus")).IsOpen);
            }
            finally { stats.TrySetResult((0, 0)); DesktopFeedback.Requested -= Feedback; await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task RealPageActivationRestoresUpdateCheckButtonAfterOldCheckEnds()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync(); var store = new DesktopSettingsStore(fixture);
            await using var theme = new ThemeService(store);
            var packages = new PluginPackageManager(new PluginInstallationPaths(Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "plugins")), new PluginPackageVerifier(new Version(2, 1, 2)));
            var plugins = new PluginManagementService(packages, new IdlePluginHost());
            await using var rollback = new RollbackDataStore(fixture);
            var page = new AppSettingsPage(null!, plugins, store, theme, null!, new InstalledAppContext(null), rollback);
            var check = new TaskCompletionSource<AppUpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0; page.ReadUpdateCheckAsync = _ => { calls++; return check.Task; };
            try
            {
                await AttachAsync(root, page); await UntilAsync(() => Field(page, "_loadingDesktopSettings") is false);
                var button = (Button)page.FindName("UpdateCheckButton"); Invoke(button); await UntilAsync(() => calls == 1);
                Assert.False(button.IsEnabled); Assert.Equal("检查中…", button.Content);
                await DetachAsync(root, page); await AttachAsync(root, page);
                check.SetResult(new AppUpdateCheckResult.Legacy(new(UpdateCheckStatus.NoUpdate, null, null, null)));
                await (Task)Field(page, "_pendingOperation")!; await UntilAsync(() => Field(page, "_loadingDesktopSettings") is false);
                Assert.True(button.IsEnabled); Assert.Equal("检查", button.Content);
                Assert.True(((AniMeido.Plugin.Base.Views.Controls.DesktopChoice)page.FindName("UpdateChannelCombo")).IsEnabled);
            }
            finally
            {
                check.TrySetResult(new AppUpdateCheckResult.Legacy(new(UpdateCheckStatus.NoUpdate, null, null, null)));
                await DetachAsync(root, page); await DesktopUiWork.DrainAsync();
            }
        });

    [Fact]
    public Task ScreenshotMetadataSaveKeepsSharedAdmissionUntilCommit()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync(); var page = fixture.Archives(new Source());
            await SeedShotAsync(fixture); await page.OnNavigatedToAsync("audit-shot");
            var targets = 0; var settings = 0;
            page.ReadScreenshotTargetsAsync = _ => { targets++; return Task.FromResult<IReadOnlyList<LocalAnimeTarget>>([]); };
            page.ReadScreenshotSettingsAsync = _ => { settings++; return Task.FromResult(Settings(fixture)); };
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                Call(page, "OnEditScreenshotClick"); await DialogAsync(page);
                var dialog = (ContentDialog)Field(page, "_dialog")!;
                Assert.IsType<StackPanel>(dialog.Content).Children.OfType<TextBox>().Single(t => t.Header as string == "截图备注").Text = "新备注";
                using (var blocker = await LockDatabaseAsync(fixture))
                {
                    Invoke(Find<Button>(dialog, b => b.Name == "PrimaryButton")); await Task.Delay(20);
                    Assert.False(page.PendingOperation.IsCompleted); dialog.Hide(); await Task.Delay(20);
                    Call(page, "OnEditScreenshotClick"); Call(page, "OnScreenshotSettingsClick");
                    Assert.Equal(1, targets); Assert.Equal(0, settings);
                }
                await page.PendingOperation; await page.PendingRead;
                Assert.Equal("新备注", Assert.Single(await new ArchiveService(fixture.Factory).GetScreenshotsAsync()).ContextNote);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; await page.PendingRead; }
        });

    private sealed class IdlePluginHost : IPluginReloadHost
    {
        public event EventHandler? StatusChanged { add { } remove { } }
        public string StatusText => "未启动";
        public Task<bool> HasActivePluginUiAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static Task SeedShotAsync(Fixture fixture)
        => new ArchiveService(fixture.Factory).InsertScreenshotAsync(new("audit-shot", Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "missing.png"),
            "fixture", DateTimeOffset.Now, "合成窗口", "fixture", 640, 480, null, null, null, null, "原备注", false));

    private static void SelectChannel(AppSettingsPage page, int index)
        => ((ComboBox)Field(page.FindName("UpdateChannelCombo"), "_native")!).SelectedIndex = index;

    private static InstalledAppContext Installed(Fixture fixture)
    {
        var root = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "install");
        Directory.CreateDirectory(Path.Combine(root, "app-2.1.2")); File.WriteAllText(Path.Combine(root, "state.json"), "{}");
        return new InstalledAppContext(Path.Combine(root, "app-2.1.2", "AniMeido.App.exe"));
    }
    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(done());
    }

    private static AppSettingsPage TrayPage(DesktopSettingsStore store, ThemeService theme, InstalledAppContext? context = null)
    {
        var page = new AppSettingsPage(null!, null!, store, theme, null!, context ?? new InstalledAppContext(null), null!);
        // 本组只驱动真实开关入口，省去插件与更新服务的启动读取。
        var loaded = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), page,
            typeof(AppSettingsPage).GetMethod("OnPageLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
        ((AniMeido.Plugin.Base.Views.Controls.DesktopChoice)page.FindName("UpdateChannelCombo")).SelectedIndex = 0;
        page.Loaded -= loaded;
        var unloaded = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), page,
            typeof(AppSettingsPage).GetMethod("OnPageUnloaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
        page.Unloaded -= unloaded;
        page.Unloaded += (_, _) =>
        {
            var version = typeof(AppSettingsPage).GetField("_navigationVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
            version.SetValue(page, (int)version.GetValue(page)! + 1);
            (Field(page, "_visitCancellation") as CancellationTokenSource)?.Cancel();
        };
        return page;
    }
    private static void Ready(AppSettingsPage page)
    {
        ((ToggleSwitch)page.FindName("KeepInTrayToggle")).IsEnabled = true;
        typeof(AppSettingsPage).GetField("_loadingDesktopSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, false);
        typeof(AppSettingsPage).GetField("_visitCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, new CancellationTokenSource());
    }
    private static ScreenshotSettings Settings(Fixture fixture) => new(false, Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "shots"), false, false);
    private static object? Field(object page, string name) => page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);
    private static void Call(object page, string name) => page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [page, new RoutedEventArgs()]);
    private static async Task DialogAsync(object page)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Field(page, "_dialog") is null && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.IsType<ContentDialog>(Field(page, "_dialog")); await Task.Delay(20);
    }
}
