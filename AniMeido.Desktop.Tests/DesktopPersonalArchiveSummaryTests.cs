using AniMeido.Plugin.Base.ViewModels;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopPersonalArchiveSummaryTests
{
    [Fact]
    public async Task ReadUsesTheRequestedAnimeAndKeepsPersonalRatingAndTags()
    {
        var calls = new List<int>();
        var reader = new DesktopPersonalArchiveSummaryReader(
            (id, _) => { calls.Add(id); return Task.FromResult<double?>(8.5); },
            (id, _) => { calls.Add(id); return Task.FromResult<IReadOnlyList<string>>(["治愈", "将棋"]); });

        var result = await reader.ReadAsync(42, () => true, default);

        Assert.NotNull(result);
        Assert.Equal(8.5, result.Rating);
        Assert.Equal(["治愈", "将棋"], result.Tags);
        Assert.Equal([42, 42], calls);
    }

    [Fact]
    public async Task MissingArchiveStaysUnratedWithoutCreatingAnything()
    {
        var reader = new DesktopPersonalArchiveSummaryReader(
            (_, _) => Task.FromResult<double?>(null),
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]));

        var result = await reader.ReadAsync(42, () => true, default);

        Assert.NotNull(result);
        Assert.Null(result.Rating);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public async Task LeavingDuringArchiveReadStopsBeforeReadingTags()
    {
        var pending = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = true;
        var tagReads = 0;
        var reader = new DesktopPersonalArchiveSummaryReader(
            (_, _) => pending.Task,
            (_, _) => { ++tagReads; return Task.FromResult<IReadOnlyList<string>>([]); });

        var read = reader.ReadAsync(42, () => current, default);
        current = false;
        pending.SetResult(8.5);

        Assert.Null(await read);
        Assert.Equal(0, tagReads);
    }

    [Fact]
    public async Task CancellationDuringIgnoredTagReadDoesNotPublish()
    {
        using var cancellation = new CancellationTokenSource();
        var tags = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new DesktopPersonalArchiveSummaryReader(
            (_, _) => Task.FromResult<double?>(8.5),
            (_, _) => tags.Task);

        var read = reader.ReadAsync(42, () => true, cancellation.Token);
        cancellation.Cancel();
        tags.SetResult(["旧标签"]);

        Assert.Null(await read);
    }

    [Fact]
    public async Task RefreshAfterSaveWinsOverAnOlderPendingRead()
    {
        var oldRating = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var reader = new DesktopPersonalArchiveSummaryReader(
            (_, _) => ++reads == 1 ? oldRating.Task : Task.FromResult<double?>(9),
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["新标签"]));

        var initial = reader.ReadAsync(42, () => true, default);
        var refreshed = await reader.ReadAsync(42, () => true, default);
        oldRating.SetResult(6);

        Assert.NotNull(refreshed);
        Assert.Equal(9, refreshed.Rating);
        Assert.Equal(["新标签"], refreshed.Tags);
        Assert.Null(await initial);
    }

    [Fact]
    public async Task FailureCanBeObservedAndTheNextReadCanSucceed()
    {
        var fail = true;
        var reader = new DesktopPersonalArchiveSummaryReader(
            (_, _) => fail ? Task.FromException<double?>(new IOException("read failed"))
                : Task.FromResult<double?>(8.5),
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["治愈"]));

        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(42, () => true, default));
        fail = false;
        Assert.NotNull(await reader.ReadAsync(42, () => true, default));
    }

    [Fact]
    public async Task InactiveOrPreCanceledVisitDoesNotStartReading()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reads = 0;
        var reader = new DesktopPersonalArchiveSummaryReader(
            (_, _) => { ++reads; return Task.FromResult<double?>(null); },
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]));

        Assert.Null(await reader.ReadAsync(42, () => false, default));
        Assert.Null(await reader.ReadAsync(42, () => true, cancellation.Token));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task AnInactiveLateCallerDoesNotInvalidateTheCurrentRead()
    {
        var rating = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new DesktopPersonalArchiveSummaryReader(
            (_, _) => rating.Task,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["当前标签"]));

        var currentRead = reader.ReadAsync(42, () => true, default);
        Assert.Null(await reader.ReadAsync(7, () => false, default));
        rating.SetResult(8.5);

        var current = await currentRead;
        Assert.NotNull(current);
        Assert.Equal(["当前标签"], current.Tags);
    }
}
