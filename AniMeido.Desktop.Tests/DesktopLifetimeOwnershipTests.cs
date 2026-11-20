using System.Xml.Linq;
using AniMeido.App.Services;
using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using AniMeido.App.Views;
using AniMeido.PluginProtocol;
using System.Reflection;
using Microsoft.UI.Xaml;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopLifetimeOwnershipTests
{
    [Theory]
    [InlineData("AniMeido.Desktop/AniMeido.Desktop.csproj", "../AniMeido.App/**/*.cs")]
    [InlineData("Plugins/AniMeido.Plugin.Base.Desktop/AniMeido.Plugin.Base.Desktop.csproj", "../AniMeido.Plugin.Base/**/*.cs")]
    public void DesktopCompileSetsSeparateOwnedUiFromReusableServices(string project, string legacyWildcard)
    {
        var document = XDocument.Load(Path.Combine(Repository(), project));
        Assert.DoesNotContain(document.Descendants("Compile"), element => (string?)element.Attribute("Include") == legacyWildcard);
        Assert.DoesNotContain(document.Descendants("Page"), element => ((string?)element.Attribute("Include"))?.Contains("../", StringComparison.Ordinal) == true);
    }

    internal static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopUiLifetimeTests
{
    [Fact]
    public Task ShutdownWaitsForAnAcceptedQueuedSaveBeforeStoppingTheDatabaseAndPlugin()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.Mine(new Source());
            var tracking = new TrackingService(fixture.Factory);
            await tracking.SetStatusAsync(1, AniMeido.Contracts.Models.AnimeTrackingStatus.Watching);
            await AttachAsync(root, page); await page.PendingActivation;
            var plugin = new DisposalProbe();
            Task stopping;
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                _ = page.ChangeEntriesAsync(page.ViewModel.Entries.ToArray(), AniMeido.Contracts.Models.AnimeTrackingStatus.Completed);
                _ = page.ChangeEntriesAsync(page.ViewModel.Entries.ToArray(), AniMeido.Contracts.Models.AnimeTrackingStatus.Following);
                await DetachAsync(root, page);
                stopping = PluginStartup.StopAfterDataDrainAsync([plugin], fixture.Factory);
                Assert.False(stopping.IsCompleted); Assert.False(plugin.Disposed);
            }
            await stopping;
            Assert.True(plugin.Disposed);
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = "SELECT Status FROM tracking WHERE AnimeId=1";
            Assert.Equal((long)AniMeido.Contracts.Models.AnimeTrackingStatus.Following, await command.ExecuteScalarAsync());
        });

    [Fact]
    public Task WorkAddedWhileDrainingIsAlsoAwaited()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DesktopUiWork.Track(first.Task);
            var draining = DesktopUiWork.DrainAsync(); Assert.False(draining.IsCompleted);
            DesktopUiWork.Track(second.Task); first.SetResult(); await Task.Delay(20);
            Assert.False(draining.IsCompleted);
            second.SetResult(); await draining;
        });

    [Fact]
    public Task DesktopCompositionKeepsRequiredPagesAndOptionalPlayerBoundary()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            var services = new ServiceCollection();
            var (items, plugins) = await PluginStartup.LoadPluginsAsync(services);
            Assert.Single(plugins); Assert.True(plugins[0].IsRequired);
            Assert.Equal(9, items.Count);
            Assert.All(items, item => Assert.Equal(typeof(TodayPage).Assembly, item.PageType!.Assembly));
            Assert.Equal(2, items.Count(item => item.IsSettingsPage));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ArchiveService));
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.FullName?.Contains("Plugin.Player", StringComparison.Ordinal) == true);
        });

    [Fact]
    public Task ScreenshotDisposalWaitsForItsUiQueueWithoutChangingData()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            fixture.Archives(new Source());
            var action = fixture.ScreenshotShortcut!;
            await Task.Run(() => action.DisposeAsync().AsTask());
            Assert.False(action.IsEnabled);
            await action.DisposeAsync();
            Assert.Empty(await new ArchiveService(fixture.Factory).GetScreenshotsAsync());
        });

    [Fact]
    public Task HostedSettingsOpenIsExclusiveAndOldFeedbackStaysDetached()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var contributions = new PluginContributionRegistry { SettingsInvoker = (_, _) => { calls++; return pending.Task; } };
            var page = new HostedPluginSettingsPage(contributions, new("fixture", "合成插件", "settings", "插件设置", ""));
            try
            {
                await AttachAsync(root, page);
                var open = (Button)page.FindName("OpenButton"); Invoke(open);
                typeof(HostedPluginSettingsPage).GetMethod("OnOpenClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [open, new RoutedEventArgs()]);
                Assert.Equal(1, calls); Assert.False(open.IsEnabled);
                await DetachAsync(root, page); await AttachAsync(root, page);
                pending.SetResult(); await page.PendingOperation;
                Assert.False(((InfoBar)page.FindName("StatusBar")).IsOpen);
                Assert.True(open.IsEnabled);
            }
            finally { pending.TrySetResult(); await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task HostedSettingsFailureRestoresTheOpenAction()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var contributions = new PluginContributionRegistry { SettingsInvoker = (_, _) => Task.FromException(new IOException("合成通信错误")) };
            var page = new HostedPluginSettingsPage(contributions, new("fixture", "合成插件", "settings", "插件设置", ""));
            try
            {
                await AttachAsync(root, page); Invoke((Button)page.FindName("OpenButton")); await page.PendingOperation;
                Assert.Equal(InfoBarSeverity.Error, ((InfoBar)page.FindName("StatusBar")).Severity);
                Assert.Contains("合成通信错误", ((InfoBar)page.FindName("StatusBar")).Message);
                Assert.True(((Button)page.FindName("OpenButton")).IsEnabled);
            }
            finally { await DetachAsync(root, page); await page.PendingOperation; }
        });

    [Fact]
    public Task NotesRenderHeadingsEntitiesAndImageLabelsAsLocalText()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            var preview = new RichTextBlock();
            ArchiveMarkdownRenderer.Render(preview, "# 作品笔记\n\n正文 &amp; [网站](https://example.invalid) ![封面](https://example.invalid/image.png)");
            var paragraphs = preview.Blocks.OfType<Microsoft.UI.Xaml.Documents.Paragraph>().ToArray();
            Assert.Equal(2, paragraphs.Length);
            Assert.Equal(25, paragraphs[0].FontSize);
            var text = string.Concat(paragraphs.SelectMany(p => p.Inlines).OfType<Microsoft.UI.Xaml.Documents.Run>().Select(run => run.Text));
            Assert.Contains("正文 & 网站 [图片] 封面", text);
            Assert.DoesNotContain(paragraphs.SelectMany(p => p.Inlines), inline => inline is Microsoft.UI.Xaml.Documents.Hyperlink);
            await Task.CompletedTask;
        });

    private sealed class DisposalProbe : IPlugin, IAsyncDisposable
    {
        internal bool Disposed { get; private set; }
        public string PluginID => "fixture";
        public string DisplayName => "合成插件";
        public string Version => "1.0.0";
        public bool IsRequired => true;
        public Task InitializeAsync(IServiceCollection services) => Task.CompletedTask;
        public IEnumerable<PluginNavigationItem> GetNavigationItems() => [];
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
