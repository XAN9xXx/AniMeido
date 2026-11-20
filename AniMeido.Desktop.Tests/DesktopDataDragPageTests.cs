using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Reflection;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopDataDragPageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ManualBackupCommitsOnceEvenIfItsSuccessNotificationCannotBeShown(bool rejectNotification)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.DataSettings();
            var messages = new List<(string Text, bool Error)>();
            void Notify(string text, bool error)
            {
                messages.Add((text, error));
                if (rejectNotification && !error) throw new InvalidOperationException("测试通知窗口已关闭");
            }

            await AttachAsync(root, page);
            await page.PendingActivation;
            DesktopFeedback.Requested += Notify;
            try
            {
                var backup = (Button)page.FindName("BackupNowButton");
                Invoke(backup);
                await page.PendingOperation;
                Assert.Single(Directory.GetFiles(fixture.BackupDirectory, "*.db"));
                Assert.Equal(("数据库已备份。", false), Assert.Single(messages));
                Assert.True(backup.IsEnabled);
            }
            finally
            {
                DesktopFeedback.Requested -= Notify;
                await DetachAsync(root, page);
            }
        });

    [Fact]
    public Task DragZonesReadOnlyAfterMounting()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new PersonalFeatureStateStore(fixture.Factory);
            await store.SaveDragZoneConfigAsync([new() { Id = "saved", Label = "保存区域" }]);
            var page = new DragZoneSettingsPage(store, NullLogger<DragZoneSettingsPage>.Instance);
            await Task.Delay(100);
            var zones = Assert.IsType<List<DragZoneConfig>>(typeof(DragZoneSettingsPage).GetField("_dragZones", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
            Assert.DoesNotContain(zones, zone => zone.Id == "saved");
        });

    [Fact]
    public Task DragZoneChangesFinishAndReturnLoadsTheLastQueuedSnapshot()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new PersonalFeatureStateStore(fixture.Factory);
            var page = new DragZoneSettingsPage(store, NullLogger<DragZoneSettingsPage>.Instance);
            await AttachAsync(root, page); await page.PendingActivation;
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                page.SetBounds("tl", 0.2, 0.3, 0.4, 0.4);
                page.SetBounds("tl", 0.7, 0.8, 0.5, 0.5);
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page); await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            try
            {
                await page.PendingActivation; await page.PendingOperation;
                var zone = (await store.LoadDragZoneConfigAsync()).Single(z => z.Id == "tl");
                Assert.Equal(0.5, zone.XPercent); Assert.Equal(0.5, zone.YPercent);
                Assert.Equal(0.5, zone.WidthPercent);
                Assert.Equal(4, ((ItemsControl)page.FindName("ZoneConfigList")).Items.Count);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task DragZoneAddDeleteAndResetAreSavedImmediately()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new PersonalFeatureStateStore(fixture.Factory);
            var page = new DragZoneSettingsPage(store, NullLogger<DragZoneSettingsPage>.Instance);
            try
            {
                await AttachAsync(root, page); await page.PendingActivation;
                Invoke((Button)page.FindName("AddZoneButton")); await page.PendingOperation;
                Assert.Equal(5, (await store.LoadDragZoneConfigAsync()).Count);
                await Task.Delay(20); root.UpdateLayout();
                Invoke(Find<Button>(page, b => b.Content as string == "删除")); await page.PendingOperation;
                Assert.Equal(4, (await store.LoadDragZoneConfigAsync()).Count);
                Invoke(Find<Button>(page, b => b.Content as string == "恢复默认")); await page.PendingOperation;
                Assert.Equal(DragZoneConfig.GetDefaults().Select(z => z.Id), (await store.LoadDragZoneConfigAsync()).Select(z => z.Id));
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task CancelledPickerAndDuplicateExportKeepDataUntouched()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.DataSettings();
            var choice = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            page.SelectFileAsync = (_, _) => { calls++; return choice.Task; };
            try
            {
                await AttachAsync(root, page); await page.PendingActivation;
                var export = (Button)page.FindName("ExportButton");
                Invoke(export);
                Assert.Throws<Microsoft.UI.Xaml.Automation.ElementNotEnabledException>(() => Invoke(export));
                typeof(DataManagementSettingsPage).GetMethod("OnExportClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [export, new RoutedEventArgs()]);
                Assert.False(export.IsEnabled); Assert.Equal(1, calls);
                choice.SetResult(null); await page.PendingOperation;
                Assert.True(export.IsEnabled); Assert.Empty(Directory.GetFiles(fixture.BackupDirectory));
            }
            finally { choice.TrySetResult(null); await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task AJsonImportCancelsBeforeConfirmationAndCommitsAfterConfirmation()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = new ArchiveService(fixture.Factory);
            await archive.UpsertArchiveAsync(1, "作品1", 7, "笔记");
            var export = new ExportService(fixture.Factory);
            var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "synthetic.json");
            await File.WriteAllTextAsync(path, await export.ExportAsync());
            var page = fixture.DataSettings(); page.SelectFileAsync = (_, _) => Task.FromResult<string?>(path);
            try
            {
                await AttachAsync(root, page); await page.PendingActivation;
                Invoke((Button)page.FindName("ImportButton"));
                await DialogAsync(page); await DetachAsync(root, page); await page.PendingOperation;
                Assert.Empty(Directory.GetFiles(fixture.BackupDirectory));
                await AttachAsync(root, page); await page.PendingActivation;
                Invoke((Button)page.FindName("ImportButton"));
                var dialog = await DialogAsync(page);
                Invoke(Find<Button>(dialog, b => b.Name == "PrimaryButton"));
                await page.PendingOperation;
                Assert.Single(Directory.GetFiles(fixture.BackupDirectory, "*.db"));
                Assert.Equal("笔记", (await archive.GetArchiveAsync(1))!.SummaryNote);
                Assert.Contains("已导入", ((InfoBar)page.FindName("DataStatus")).Message);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task ExportContinuesAfterAcceptedDestinationAndReturningWaitsForIt()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.DataSettings();
            var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "export.json");
            page.SelectFileAsync = (_, _) => Task.FromResult<string?>(path);
            await AttachAsync(root, page); await page.PendingActivation;
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                Invoke((Button)page.FindName("ExportButton"));
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page); await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            try
            {
                await page.PendingActivation;
                Assert.NotNull(await new PersonalDataTransferService(fixture.Factory, new ExportService(fixture.Factory), null!, null!)
                    .PreviewJsonAsync(await File.ReadAllTextAsync(path)));
                Assert.True(((Button)page.FindName("ExportButton")).IsEnabled);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    private static async Task<ContentDialog> DialogAsync(DataManagementSettingsPage page)
    {
        var field = typeof(DataManagementSettingsPage).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (field.GetValue(page) is null && DateTime.UtcNow < deadline) await Task.Delay(10);
        var dialog = Assert.IsType<ContentDialog>(field.GetValue(page)); await Task.Delay(20); return dialog;
    }

    [Fact]
    public Task InvalidImportShowsAnErrorAndRestoresActions()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "invalid.json");
            await File.WriteAllTextAsync(path, "{");
            var page = fixture.DataSettings(); page.SelectFileAsync = (_, _) => Task.FromResult<string?>(path);
            try
            {
                await AttachAsync(root, page); await page.PendingActivation;
                Invoke((Button)page.FindName("ImportButton")); await page.PendingOperation;
                Assert.True(((InfoBar)page.FindName("DataStatus")).IsOpen);
                Assert.Equal(InfoBarSeverity.Error, ((InfoBar)page.FindName("DataStatus")).Severity);
                Assert.True(((Button)page.FindName("ImportButton")).IsEnabled);
                Assert.Empty(Directory.GetFiles(fixture.BackupDirectory));
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task ScreenshotFeedbackUsesTheSameThemeAndReadableErrorContent()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var content = CaptureFeedbackWindow.CreateContent(null, "窗口暂时无法捕获", out var image);
            root.Children.Add(content); root.UpdateLayout();
            Assert.Null(image);
            Assert.Same(Application.Current.Resources["AniMeidoCardStyle"], content.Style);
            Assert.Equal("截图失败", Find<TextBlock>(content, text => text.Text == "截图失败").Text);
            Assert.Equal("窗口暂时无法捕获", Find<TextBlock>(content, text => text.Text == "窗口暂时无法捕获").Text);
            await Task.CompletedTask;
        });
}
