using System.Reflection;
using AniMeido.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed class NavigationShutdownTests
{
    [Fact]
    public void Stop_IgnoresLateNavigationAndBackRequestsWithoutPublishingNavigation()
    {
        var navigation = CreateNavigation();
        var notifications = 0;
        navigation.Navigated += _ => notifications++;

        navigation.Stop();
        navigation.Navigate(typeof(object), "late detail");
        navigation.NavigateTopLevel(typeof(object));
        navigation.GoBack();
        navigation.Stop();

        Assert.Equal(0, notifications);
        Assert.Null(navigation.CurrentPageType);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void Stop_ReleasesTheCachedStackAndCurrentIdentity()
    {
        var navigation = CreateNavigation();
        var stack = Assert.IsType<NavigationStack>(typeof(NavigationService)
            .GetField("_backStack", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(navigation));
        stack.Push(new object(), new object());
        var parameterField = typeof(NavigationService)
            .GetField("_lastParameter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        parameterField.SetValue(navigation, new object());
        typeof(NavigationService).GetProperty(nameof(NavigationService.CurrentPageType))!
            .SetValue(navigation, typeof(object));
        Assert.True(navigation.CanGoBack);

        navigation.Stop();

        Assert.Equal(0, stack.Count);
        Assert.False(navigation.CanGoBack);
        Assert.Null(navigation.CurrentPageType);
        Assert.Null(parameterField.GetValue(navigation));
    }

    [Fact]
    public void Stop_PreventsInitializationFromReopeningNavigation()
    {
        var navigation = CreateNavigation();
        navigation.Stop();

        // Stop 必须先拒绝；不创建或使用 WinUI Frame。
        Assert.Throws<ObjectDisposedException>(() => navigation.Initialize(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_DoesNotRewriteTheHistoricalFirstNavigationSignal(bool completed)
    {
        var navigation = CreateNavigation();
        if (completed)
            navigation.FirstNavigationCompleted.SetResult();

        navigation.Stop();

        Assert.Equal(completed, navigation.FirstNavigationCompleted.Task.IsCompletedSuccessfully);
        Assert.False(navigation.FirstNavigationCompleted.Task.IsCanceled);
        Assert.False(navigation.FirstNavigationCompleted.Task.IsFaulted);
    }

    [Fact]
    public void BeforeStop_NavigationStillRequiresInitialization()
    {
        var navigation = CreateNavigation();

        Assert.Throws<InvalidOperationException>(() => navigation.Navigate(typeof(object)));
        Assert.Throws<InvalidOperationException>(() => navigation.NavigateTopLevel(typeof(object)));
        Assert.False(navigation.FirstNavigationCompleted.Task.IsCompleted);
    }

    private static NavigationService CreateNavigation()
        => new(new PageFactory(new UnexpectedServices()), NullLogger<NavigationService>.Instance);

    private sealed class UnexpectedServices : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => throw new InvalidOperationException("Stopped navigation must not create pages.");
    }
}
