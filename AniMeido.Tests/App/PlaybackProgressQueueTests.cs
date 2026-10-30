using AniMeido.Contracts.Playback;
using AniMeido.PluginProtocol;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class PlaybackProgressQueueTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentReports_AreDeliveredInSequenceOrder()
    {
        var (reporter, snapshot, acknowledge) = CreateQueue();
        for (var batch = 0; batch < 20; batch++)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reports = Enumerable.Range(0, 128).Select(index => Task.Run(async () =>
            {
                await start.Task;
                await reporter.ReportAsync(Progress($"{batch}-{index}"));
            })).ToArray();
            start.SetResult();
            await Task.WhenAll(reports).WaitAsync(Budget);

            var events = snapshot();
            Assert.Equal(128, events.Length);
            Assert.Equal(events.Select(item => item.Sequence).Order(), events.Select(item => item.Sequence));
            Assert.Equal(128, events.Select(item => item.EventId).Distinct().Count());
            acknowledge(events[^1].Sequence);
            Assert.Empty(snapshot());
        }
    }

    [Fact]
    public async Task AcknowledgeSnapshot_DoesNotRemoveAnEventReportedAfterSnapshot()
    {
        var (reporter, snapshot, acknowledge) = CreateQueue();
        await reporter.ReportAsync(Progress("delivered"));
        var delivered = Assert.Single(snapshot());
        await Task.Run(() => reporter.ReportAsync(Progress("not-delivered"))).WaitAsync(Budget);
        acknowledge(delivered.Sequence);

        Assert.Equal("not-delivered", Assert.Single(snapshot()).EventId);
    }

    private static AnimePlaybackProgress Progress(string id)
        => new(id, 1, 1, 10, 100, false, DateTimeOffset.UtcNow);

    private static (IAnimePlaybackProgressReporter Reporter,
        Func<HostedPlaybackProgressEvent[]> Snapshot, Action<long> Acknowledge) CreateQueue()
    {
        var assembly = Assembly.LoadFrom(AppDataRootsTests.BuildOutput("AniMeido.PluginHost"));
        var type = assembly.GetType("AniMeido.PluginHost.PlaybackProgressEventQueue", throwOnError: true)!;
        var instance = Activator.CreateInstance(type, nonPublic: true)!;
        var snapshot = type.GetMethod("GetPendingEvents", BindingFlags.Public | BindingFlags.Instance)!;
        var acknowledge = type.GetMethod("Acknowledge", BindingFlags.Public | BindingFlags.Instance)!;
        return ((IAnimePlaybackProgressReporter)instance,
            () => (HostedPlaybackProgressEvent[])snapshot.Invoke(instance, null)!,
            sequence => acknowledge.Invoke(instance, [sequence]));
    }
}
