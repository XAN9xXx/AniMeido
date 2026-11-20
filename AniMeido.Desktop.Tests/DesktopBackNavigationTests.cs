using AniMeido.App;
using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Reflection;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopBackNavigationTests
{
    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10)]
    [InlineData(DesktopVisualStyle.Fluent11)]
    [InlineData(DesktopVisualStyle.Paper)]
    public Task ActualShellBackButtonRestoresThePreviousPageInstanceAndItsState(DesktopVisualStyle visual)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = visual.ToString(), ThemeSetting = "Dark" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            using var provider = new ServiceCollection().BuildServiceProvider();
            var navigation = new NavigationService(new PageFactory(provider), NullLogger<NavigationService>.Instance);
            var window = new MainWindow(new PluginContributionRegistry(), navigation, theme, null!,
                new MainWindowDragDropAdapter(() => null!));
            var shell = Assert.IsType<Grid>(window.Content);
            var loaded = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
                typeof(MainWindow).GetMethod("OnShellLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
            shell.Loaded -= loaded;
            root.Children.Add(shell);
            try
            {
                var button = (Button)shell.FindName("BackButton");
                var frame = (Frame)shell.FindName("ContentFrame");
                navigation.NavigateTopLevel(typeof(SearchStatePage));
                await Task.Delay(20);
                var first = Assert.IsType<SearchStatePage>(frame.Content);
                first.Query.Text = "保留的搜索";
                first.Scroll.ChangeView(null, 180, null, true);
                await Task.Delay(20);
                var offset = first.Scroll.VerticalOffset;
                Assert.True(offset > 0);
                navigation.Navigate(typeof(DetailStatePage), 42);
                await Task.Delay(20);
                Assert.True(navigation.CanGoBack);
                Assert.True(button.IsEnabled);
                Invoke(button);
                await Task.Delay(20);
                Assert.Same(first, frame.Content);
                Assert.Equal("保留的搜索", first.Query.Text);
                Assert.Equal(offset, first.Scroll.VerticalOffset);
                Assert.False(navigation.CanGoBack);
                Assert.False(button.IsEnabled);
                Assert.Equal(typeof(SearchStatePage), navigation.CurrentPageType);
            }
            finally { window.BeginShutdown(); root.Children.Remove(shell); window.Close(); }
        });

    public sealed class SearchStatePage : Page
    {
        internal TextBox Query { get; } = new();
        internal ScrollViewer Scroll { get; } = new() { Height = 300, Content = new Border { Height = 2000 } };
        public SearchStatePage() => Content = new StackPanel { Children = { Query, Scroll } };
    }

    public sealed class DetailStatePage : Page, INavigationAware
    {
        public Task OnNavigatedToAsync(object? parameter) => Task.CompletedTask;
    }
}
