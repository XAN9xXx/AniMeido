using AniMeido.App.Services;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopWindowCloseTests
{
    [Fact]
    public Task FinalCloseWaitsUntilTheCurrentCallbackReturns()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var closingCallback = true;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DesktopWindowClose.Enqueue(root.DispatcherQueue, () =>
            {
                try
                {
                    Assert.False(closingCallback);
                    Assert.True(root.DispatcherQueue.HasThreadAccess);
                    closed.SetResult();
                }
                catch (Xunit.Sdk.XunitException error) { closed.SetException(error); }
            });
            Assert.False(closed.Task.IsCompleted);
            closingCallback = false;
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
}
