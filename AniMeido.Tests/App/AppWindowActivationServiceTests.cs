using AniMeido.App.Services;
using AniMeido.Contracts.Desktop;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class AppWindowActivationServiceTests
{
    [Fact]
    public void WindowHandleProvider_IsInvalidBeforeAttachAndAfterDetach()
    {
        var service = new AppWindowActivationService();

        Assert.False(service.TryGetWindowHandle(out var initialHandle));
        Assert.Equal(nint.Zero, initialHandle);

        typeof(AppWindowActivationService)
            .GetField("_windowHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, (nint)1234);
        Assert.True(service.TryGetWindowHandle(out var attachedHandle));
        Assert.Equal((nint)1234, attachedHandle);

        service.Detach();

        Assert.False(service.TryGetWindowHandle(out var detachedHandle));
        Assert.Equal(nint.Zero, detachedHandle);
    }

    [Fact]
    public void WindowHandleProviderAndActivationUseTheSameSingleton()
    {
        using var provider = new ServiceCollection()
            .AddAppServices()
            .BuildServiceProvider();

        var activation = provider
            .GetRequiredService<IAppWindowActivationService>();
        var handleProvider = provider
            .GetRequiredService<IWindowHandleProvider>();

        Assert.Same(activation, handleProvider);
    }
}
