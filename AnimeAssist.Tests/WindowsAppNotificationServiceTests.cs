using AniMeido.App.Services;
using System.Runtime.InteropServices;

namespace AniMeido.Tests;

public sealed class WindowsAppNotificationServiceTests
{
    [Fact]
    public void IsNotificationInfrastructureError_ClassifiesWinRtFailures()
    {
        Assert.True(
            WindowsAppNotificationService.IsNotificationInfrastructureError(
                new COMException("Element not found", unchecked(
                    (int)0x80070490))));
        Assert.True(
            WindowsAppNotificationService.IsNotificationInfrastructureError(
                new InvalidOperationException()));
        Assert.False(
            WindowsAppNotificationService.IsNotificationInfrastructureError(
                new OperationCanceledException()));
    }
}
