using AniMeido.App.Controls;
using AniMeido.App.Services;
using AniMeido.App.Themes;
using AniMeido.Contracts;
using AniMeido.Contracts.Desktop;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Xunit;
using static AniMeido.Desktop.Tests.UnoVisualEvidence;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopRefreshRouteTests
{
    [Fact]
    public Task F5RefreshesOnlyTheMountedArchiveFromSidebarBodyAndEditField()
        => RunAsync(async (root, window, navigation, frame, mouse, archive) =>
        {
            for (var i = 1; i <= 35; ++i)
                await archive.UpsertArchiveAsync(i, $"合成档案 {i:D2}", null, "");
            var side = SidebarButton(window, navigation, typeof(ArchivePage));
            await WaitUntil(() => side.IsLoaded && side.ActualWidth > 0);
            mouse.Click(side);
            var page = Assert.IsType<ArchivePage>(frame.Content);
            await WaitUntil(() => page.IsLoaded);
            await page.PendingActivation;
            Assert.Same(side, FocusManager.GetFocusedElement(root.XamlRoot!));
            var archives = Part<ListView>(page, "ArchiveList");
            var selected = archives.Items.OfType<ArchiveListItem>().Single(item => item.Archive.AnimeId == 24);
            archives.SelectedItem = selected;
            await page.PendingRead;
            var scroll = Descendants(archives).OfType<ScrollViewer>().First(viewer => viewer.ScrollableHeight > 200);
            scroll.ChangeView(null, 200, null, true);
            await WaitUntil(() => scroll.VerticalOffset == 200);
            var timeline = Part<ListView>(page, "EntryList");
            Assert.DoesNotContain(Descendants(root).OfType<TextBlock>(), text => text.IsLoaded && text.Text == "档案已刷新");
            const string sidebarNote = "从侧栏刷新可见的记录";
            await archive.AddEntryAsync(24, DateTimeOffset.Now, 1, sidebarNote);
            mouse.Key(VirtualKey.F5);
            await page.PendingRead;
            await WaitUntil(() => Sees(sidebarNote));
            await WaitUntil(() => Descendants(root).OfType<TextBlock>().Any(text => text.IsLoaded && text.Text == "档案已刷新"));
            Assert.False(Part<InfoBar>(page, "StatusInfoBar").IsOpen);
            Assert.Same(side, FocusManager.GetFocusedElement(root.XamlRoot!));
            Assert.Equal(200, scroll.VerticalOffset);
            Assert.Equal(24, Assert.IsType<ArchiveListItem>(archives.SelectedItem).Archive.AnimeId);
            await UnoVisualEvidence.CaptureAsync(root, "G-shortcuts", "archive-real-f5");

            var body = Part<ComboBox>(page, "ArchiveTimelineFilter");
            Assert.True(body.Focus(FocusState.Keyboard));
            const string bodyNote = "从正文刷新可见的记录";
            await archive.AddEntryAsync(24, DateTimeOffset.Now, 2, bodyNote);
            mouse.Key(VirtualKey.F5);
            await WaitUntil(() => Sees(bodyNote));

            var field = Part<TextBox>(page, "ArchiveFilter");
            Assert.True(field.Focus(FocusState.Keyboard));
            const string fieldNote = "在输入框也能刷新";
            await archive.AddEntryAsync(24, DateTimeOffset.Now, 3, fieldNote);
            field.Text = "合成";
            field.Select(1, 1);
            mouse.Key(VirtualKey.F5);
            await WaitUntil(() => Sees(fieldNote));
            Assert.Same(field, FocusManager.GetFocusedElement(root.XamlRoot!));
            Assert.Equal("合成", field.Text);
            Assert.Equal(1, field.SelectionStart);
            Assert.Equal(1, field.SelectionLength);
            Assert.Equal(24, Assert.IsType<ArchiveListItem>(archives.SelectedItem).Archive.AnimeId);
            Assert.Equal(200, scroll.VerticalOffset);

            const string whilePopup = "菜单打开时不应刷新";
            await archive.AddEntryAsync(24, DateTimeOffset.Now, 4, whilePopup);
            body.IsDropDownOpen = true;
            await WaitUntil(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot!).Any(p => p.IsOpen));
            mouse.Key(VirtualKey.F5);
            await page.PendingRead;
            Assert.False(Sees(whilePopup));
            body.IsDropDownOpen = false;
            await WaitUntil(() => !VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot!).Any(p => p.IsOpen));
            Assert.True(field.Focus(FocusState.Keyboard));
            await UnoVisualEvidence.CaptureAsync(root, "G-shortcuts", "archive-f5-retained");

            navigation.NavigateTopLevel(typeof(OtherPage));
            await WaitUntil(() => !page.IsLoaded && frame.Content is OtherPage { IsLoaded: true });
            Assert.True(((OtherPage)frame.Content).Input.Focus(FocusState.Keyboard));
            mouse.Key(VirtualKey.F5);
            await page.PendingRead;
            Assert.False(Sees(whilePopup));

            bool Sees(string note) => timeline.Items.OfType<ArchiveTimelineItem>().Any(item => item.Entry?.Body == note);
        });

    [Fact]
    public Task F5RefreshesRecommendationsFromTheSidebarOnceAndRendersFeedback()
        => RunAsync(async (root, window, navigation, frame, mouse, _) =>
        {
            var side = SidebarButton(window, navigation, typeof(RecommendationPage));
            await WaitUntil(() => side.IsLoaded && side.ActualWidth > 0);
            mouse.Click(side);
            var page = Assert.IsType<RecommendationPage>(frame.Content);
            await WaitUntil(() => page.IsLoaded);
            await page.PendingActivation;
            var launches = 0;
            page.ViewModel.PropertyChanged += (_, evt) =>
            {
                if (evt.PropertyName == nameof(RecommendationViewModel.IsRefreshing) && page.ViewModel.IsRefreshing) launches++;
            };
            Assert.Same(side, FocusManager.GetFocusedElement(root.XamlRoot!));
            mouse.Key(VirtualKey.F5);
            mouse.Key(VirtualKey.F5);
            await WaitUntil(() => launches == 1);
            await page.PendingOperation;
            Assert.Equal(1, launches);
            Assert.Null(page.FindName("RefreshButton"));
            var message = page.ViewModel.Message;
            Assert.False(string.IsNullOrWhiteSpace(message));
            await WaitUntil(() => Descendants(root).OfType<TextBlock>().Any(element =>
                element.IsLoaded && element.Text == message && !Descendants(page).Contains(element)));
            Assert.False(Part<InfoBar>(page, "StatusInfoBar").IsOpen);
            await UnoVisualEvidence.CaptureAsync(root, "G-shortcuts", "recommendation-real-f5");

            var body = Part<ToggleButton>(page, "RecommendationsSectionButton");
            Assert.True(body.Focus(FocusState.Keyboard));
            mouse.Key(VirtualKey.F5);
            await WaitUntil(() => launches == 2);
            await page.PendingOperation;
            navigation.NavigateTopLevel(typeof(OtherPage));
            await WaitUntil(() => !page.IsLoaded && frame.Content is OtherPage { IsLoaded: true });
            Assert.True(((OtherPage)frame.Content).Input.Focus(FocusState.Keyboard));
            mouse.Key(VirtualKey.F5);
            Assert.Equal(2, launches);
        });

    private static Task RunAsync(Func<Grid, Grid, NavigationService, Frame, DesktopTestMouse, ArchiveService, Task> action)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper", ThemeSetting = "Dark", FontStyle = "Serif" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var source = new Source { Seasonal = _ => Task.FromResult(new List<AniMeido.Contracts.Models.Anime> { Item(21), Item(22), Item(23) }) };
            var browse = State();
            _ = fixture.Recommendation(source, browse);
            var archive = new ArchiveService(fixture.Factory);
            var remap = new PersonalIdRemapService(fixture.Factory, NullLogger<PersonalIdRemapService>.Instance);
            var screenshots = new ScreenshotArchiveService(archive, null!, null!, null!);
            using var shortcut = new ScreenshotShortcutAction(screenshots, archive, new TestWindow(), fixture.Navigation);
            await using var services = new ServiceCollection()
                .AddSingleton<IAnimeDataSource>(source).AddSingleton(archive).AddSingleton(remap).AddSingleton(screenshots).AddSingleton(shortcut)
                .AddSingleton(new ArchiveQueryService(archive)).AddSingleton(new ArchiveReviewExporter(archive))
                .AddSingleton(new PendingReviewService(fixture.Factory, remap))
                .AddSingleton<IAnimePlaybackLauncher, TestPlayback>().AddSingleton<IWindowHandleProvider, TestWindow>()
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .AddSingleton(browse).AddSingleton(fixture.Recommendations!)
                .AddSingleton<PageFactory>().AddSingleton<NavigationService>()
                .AddSingleton<IPluginNavigator>(provider => provider.GetRequiredService<NavigationService>())
                .BuildServiceProvider();
            var navigation = services.GetRequiredService<NavigationService>();
            var frame = new Frame();
            var shell = new Grid { Background = (Brush)Application.Current.Resources["DesktopPageBrush"] };
            shell.ColumnDefinitions.Add(new() { Width = new(196) });
            shell.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            Grid.SetColumn(frame, 1);
            shell.Children.Add(frame);
            shell.KeyDown += (_, args) => DesktopManualRefreshShortcut.Handle(frame, args, message => theme.ShowFeedback(message));
            navigation.Initialize(frame);
            navigation.NavigateTopLevel(typeof(OtherPage));
            root.Children.Insert(0, shell);
            try { await action(root, shell, navigation, frame, new DesktopTestMouse(root), archive); }
            finally
            {
                var page = frame.Content;
                navigation.NavigateTopLevel(typeof(OtherPage));
                if (page is ArchivePage archives)
                {
                    await WaitUntil(() => !archives.IsLoaded);
                    await archives.PendingRead;
                }
                if (page is RecommendationPage recommendation)
                {
                    await WaitUntil(() => !recommendation.IsLoaded);
                    await Task.WhenAll(recommendation.PendingOperation, recommendation.PendingTags, recommendation.PendingPrefetch);
                }
                navigation.Stop();
                root.Children.Remove(shell);
            }
        });

    private static DesktopShellNavigationItem SidebarButton(Grid root, NavigationService navigation, Type target)
    {
        var route = new BasePlugin().GetNavigationItems().Single(item => item.PageType == target);
        var resources = new DesktopShellResources();
        var button = new DesktopShellNavigationItem(route.Label, route.Icon)
        {
            Style = (Style)resources["DesktopShellNavigationStyle"], VerticalAlignment = VerticalAlignment.Top,
        };
        button.ApplyAppearance(DesktopVisualStyle.Paper, true, Application.Current.Resources);
        button.Click += (_, _) => navigation.NavigateTopLevel(route.PageType!);
        root.Children.Add(button);
        return button;
    }

    private static T Part<T>(FrameworkElement element, string name) where T : FrameworkElement
        => Assert.IsAssignableFrom<T>(element.FindName(name));
    private static Task WaitUntil(Func<bool> done) => UnoVisualEvidence.WaitForAsync(done);

    private sealed class OtherPage : Page
    {
        internal TextBox Input { get; } = new() { Header = "其他页面的输入框" };
        public OtherPage() => Content = Input;
    }

    private sealed class TestWindow : IWindowHandleProvider, IAppWindowActivationService
    {
        public bool TryGetWindowHandle(out nint handle) { handle = 0; return false; }
        public void ActivateMainWindow() { }
    }

    private sealed class TestPlayback : IAnimePlaybackLauncher
    {
        public bool IsAvailable => false;
        public event EventHandler? AvailabilityChanged { add { } remove { } }
        public Task LaunchAsync(AnimePlaybackContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
