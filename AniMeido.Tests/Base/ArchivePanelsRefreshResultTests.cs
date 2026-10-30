using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class ArchivePanelsRefreshResultTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset RecordedAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CurrentSuccessfulLoad_ReturnsTrueAndKeepsBothPublicationEvents()
    {
        IReadOnlyList<ArchiveListItem> expected = [Archive(42)];
        var vm = Create(_ => Task.FromResult(expected));
        var published = new List<ArchivePanelKind>();
        var completed = new List<(ArchivePanelKind Panel, Exception? Error)>();
        vm.PanelPublished += published.Add;
        vm.PanelLoadCompleted += (panel, error) => completed.Add((panel, error));

        Assert.True(await vm.EnsureActiveAsync(2026, CancellationToken.None).WaitAsync(Budget));

        Assert.Same(expected, vm.Archives);
        Assert.Equal(new[] { ArchivePanelKind.Archives }, published);
        var completion = Assert.Single(completed);
        Assert.Equal(ArchivePanelKind.Archives, completion.Panel);
        Assert.Null(completion.Error);
    }

    [Fact]
    public async Task CurrentReadFailure_ReturnsFalseKeepsTheOriginalDataAndAllowsRetry()
    {
        IReadOnlyList<ArchiveListItem> original = [Archive(1)];
        IReadOnlyList<ArchiveListItem> replacement = [Archive(2)];
        var failure = new IOException("synthetic archive refresh failure");
        var calls = 0;
        var vm = Create(_ => ++calls switch
        {
            1 => Task.FromResult(original),
            2 => Task.FromException<IReadOnlyList<ArchiveListItem>>(failure),
            _ => Task.FromResult(replacement),
        });
        Assert.True(await vm.EnsureActiveAsync(2026, CancellationToken.None).WaitAsync(Budget));
        var published = new List<ArchivePanelKind>();
        var completed = new List<(ArchivePanelKind Panel, Exception? Error)>();
        vm.PanelPublished += published.Add;
        vm.PanelLoadCompleted += (panel, error) => completed.Add((panel, error));
        vm.Invalidate(ArchivePanelKind.Archives);

        Assert.False(await vm.EnsureActiveAsync(2026, CancellationToken.None).WaitAsync(Budget));

        Assert.Same(original, vm.Archives);
        Assert.Empty(published);
        var failed = Assert.Single(completed);
        Assert.Equal(ArchivePanelKind.Archives, failed.Panel);
        Assert.Same(failure, failed.Error);

        Assert.True(await vm.EnsureActiveAsync(2026, CancellationToken.None).WaitAsync(Budget));

        Assert.Equal(3, calls);
        Assert.Same(replacement, vm.Archives);
        Assert.Equal(new[] { ArchivePanelKind.Archives }, published);
        Assert.Equal(2, completed.Count);
        Assert.Equal(ArchivePanelKind.Archives, completed[1].Panel);
        Assert.Null(completed[1].Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteLoad_WaitsForItsActualReadThenReturnsFalseWithoutLateEvents(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<IReadOnlyList<ArchiveListItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestToken = CancellationToken.None;
        var vm = Create(token =>
        {
            requestToken = token;
            entered.TrySetResult();
            return finish.Task;
        });
        var published = new List<ArchivePanelKind>();
        var completed = new List<(ArchivePanelKind Panel, Exception? Error)>();
        vm.PanelPublished += published.Add;
        vm.PanelLoadCompleted += (panel, error) => completed.Add((panel, error));
        using var lifetime = new CancellationTokenSource();
        var loading = vm.EnsureActiveAsync(2026, lifetime.Token);
        try
        {
            await entered.Task.WaitAsync(Budget);
            if (cancel)
            {
                lifetime.Cancel();
                vm.CancelInflight();
            }
            else
            {
                vm.SelectPanel(ArchivePanelKind.Statistics);
                Assert.True(await vm.EnsureActiveAsync(2026, lifetime.Token).WaitAsync(Budget));
            }

            Assert.True(requestToken.IsCancellationRequested);
            Assert.False(loading.IsCompleted);
            var currentPublications = published.ToArray();
            var currentCompletions = completed.ToArray();
            finish.TrySetResult([Archive(99)]);

            Assert.False(await loading.WaitAsync(Budget));

            Assert.Empty(vm.Archives);
            Assert.Equal(currentPublications, published);
            Assert.Equal(currentCompletions, completed);
            if (!cancel)
            {
                Assert.Equal(7, vm.Statistics!.ArchiveCount);
                Assert.Equal(new[] { ArchivePanelKind.Statistics }, published);
            }
            else
            {
                Assert.Empty(published);
                Assert.Empty(completed);
            }
        }
        finally
        {
            finish.TrySetResult([]);
            lifetime.Cancel();
            vm.CancelInflight();
            await ObserveAsync(loading).WaitAsync(Budget);
        }
    }

    private static async Task ObserveAsync(Task task)
        => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private static ArchivePanelsViewModel Create(
        Func<CancellationToken, Task<IReadOnlyList<ArchiveListItem>>> loadArchives)
        => new(
            loadArchives,
            _ => Task.FromResult(Statistics(7)),
            (year, _) => Task.FromResult(new ArchiveReviewData(year, Statistics(0), [], [], [])),
            _ => Task.FromResult(new ArchiveScreenshotData([], new Dictionary<string, IReadOnlyList<string>>())));

    private static ArchiveListItem Archive(int id)
        => new(new AnimeArchive(id, $"作品{id}", 8, "笔记", RecordedAt, RecordedAt),
            [], 0, 0, AnimeTrackingStatus.Completed);

    private static ArchiveStatistics Statistics(int count)
        => new(RecordedAt, count, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>());
}
