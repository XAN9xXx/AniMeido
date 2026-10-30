using AniMeido.Plugin.Player.Playback;

namespace AniMeido.Tests;

public sealed class PlaybackEventLoopTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Stop_RetainsResourcesUntilTheCurrentEventReturns()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var released = 0;
        var loop = new PlaybackEventLoop(() =>
        {
            entered.Set();
            release.Wait();
        }, () => Interlocked.Increment(ref released));

        try
        {
            Assert.True(entered.Wait(Budget));
            var stopping = loop.StopAsync();
            Assert.Same(stopping, loop.StopAsync());
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref released));
        }
        finally
        {
            release.Set();
            await loop.StopAsync().WaitAsync(Budget);
        }
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task ReadFailure_ReleasesResourcesAndReachesTheOwner()
    {
        var failure = new InvalidOperationException("event failure");
        using var entered = new ManualResetEventSlim();
        var released = 0;
        var loop = new PlaybackEventLoop(() =>
        {
            entered.Set();
            throw failure;
        }, () => Interlocked.Increment(ref released));

        Assert.True(entered.Wait(Budget));
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.StopAsync().WaitAsync(Budget));
        Assert.Same(failure, observed);
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task ReleaseFailure_IsNotReportedAsSuccessfulShutdown()
    {
        var failure = new IOException("release failure");
        var loop = new PlaybackEventLoop(
            () => Thread.Yield(),
            () => throw failure);

        var observed = await Assert.ThrowsAsync<IOException>(
            () => loop.StopAsync().WaitAsync(Budget));
        Assert.Same(failure, observed);
    }
}
