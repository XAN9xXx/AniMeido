using AniMeido.Plugin.Player.Sources.Subscriptions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class GitHubSourceReaderTests(ITestOutputHelper output)
{
    private const string RepositoryUrl = "https://github.com/example/sources/tree/main/inner_source";
    private const string SourceText = "// synthetic source; never executed";

    [Theory]
    [InlineData(null)]
    [InlineData("main")]
    [InlineData("v1.0")]
    [InlineData("0123456789abcdef")]
    public async Task Read_UsesOnePinnedCommitForTheTreeAndEveryFile(string? reference)
    {
        using var handler = new SourceHandler(2, (_, _) => Task.FromResult(Text(SourceText)));
        using var client = new HttpClient(handler);
        var reader = new GitHubSourceReader(client);
        var url = reference is null ? "https://github.com/example/sources"
            : RepositoryUrl.Replace("/tree/main/", $"/tree/{reference}/");

        var files = await reader.ReadAsync(url, SourceSubscriptionKind.EasyBangumi, default);

        Assert.Equal(2, files.Count);
        Assert.Single(handler.Requests, uri => uri.AbsolutePath == $"/repos/example/sources/commits/{reference ?? "main"}");
        Assert.Single(handler.Requests, uri => uri.AbsolutePath == "/repos/example/sources/git/trees/fixed-tree");
        Assert.All(handler.Requests.Where(uri => uri.Host == "raw.githubusercontent.com"),
            uri => Assert.StartsWith("/example/sources/fixed-commit/", uri.AbsolutePath));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task Read_DoesNotRequireTheCallingUiContextToResume()
    {
        var ui = new PausedUiContext();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = TimeSpan.FromSeconds(5);
        using var handler = new SourceHandler(1, async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(budget, token).ConfigureAwait(false);
            return Text(SourceText);
        });
        using var client = new HttpClient(handler);
        var reader = new GitHubSourceReader(client);
        Task<IReadOnlyList<GitHubSourceFile>> read;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            read = reader.ReadAsync(RepositoryUrl, SourceSubscriptionKind.EasyBangumi, default);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        try
        {
            await started.Task.WaitAsync(budget);
            release.TrySetResult();
            Assert.Equal(SourceText, Assert.Single(await read.WaitAsync(budget)).Content);
            Assert.Equal(0, ui.PostCount);
        }
        finally
        {
            release.TrySetResult();
            ui.Resume();
            await read.WaitAsync(budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_StopsNewWorkAndWaitsForAcceptedRequests(bool userCancelled)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = TimeSpan.FromSeconds(5);
        var requests = 0;
        using var cancellation = new CancellationTokenSource();
        using var handler = new SourceHandler(20, async (index, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            if (Interlocked.Increment(ref requests) == 6)
            {
                started.TrySetResult();
            }

            if (index == 0 && !userCancelled)
            {
                await fail.Task.WaitAsync(budget);
                throw new HttpRequestException("Synthetic source failure.");
            }

            if (index == 1)
            {
                // 模拟已接纳、却暂时不响应取消的真实读取。
                await release.Task.WaitAsync(budget);
            }
            else
            {
                await release.Task.WaitAsync(budget, token);
            }

            return Text(SourceText);
        });
        using var client = new HttpClient(handler);
        var reader = new GitHubSourceReader(client);
        var read = reader.ReadAsync(RepositoryUrl, SourceSubscriptionKind.EasyBangumi, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(budget);
            if (userCancelled)
            {
                cancellation.Cancel();
            }
            else
            {
                fail.TrySetResult();
            }

            await cancelled.Task.WaitAsync(budget);
            Assert.False(read.IsCompleted);
            Assert.Equal(6, Volatile.Read(ref requests));
        }
        finally
        {
            fail.TrySetResult();
            release.TrySetResult();
            if (userCancelled)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(budget));
            }
            else
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => read.WaitAsync(budget));
            }
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [Trait("Category", "Performance")]
    public async Task ReadCost_KeepsEveryFileAndStablePathOrder(int count)
    {
        using var handler = new SourceHandler(count, (_, _) => Task.FromResult(Text(SourceText)));
        using var client = new HttpClient(handler);
        var reader = new GitHubSourceReader(client);
        for (var warmup = 0; warmup < 3; warmup++)
        {
            await reader.ReadAsync(RepositoryUrl, SourceSubscriptionKind.EasyBangumi, default);
        }

        var samples = new List<(double Milliseconds, long Bytes)>();
        for (var sample = 0; sample < 5; sample++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var timer = Stopwatch.StartNew();
            var files = await reader.ReadAsync(RepositoryUrl, SourceSubscriptionKind.EasyBangumi, default);
            timer.Stop();
            samples.Add((timer.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - before));
            Assert.Equal(count, files.Count);
            Assert.Equal(Enumerable.Range(0, count).Select(PathFor), files.Select(file => file.Path));
            Assert.All(files, file => Assert.Equal(SourceText, file.Content));
        }

        output.WriteLine("{0} source files: median {1:F3} ms, {2:N0} B", count,
            samples.Select(sample => sample.Milliseconds).Order().ElementAt(2),
            samples.Select(sample => sample.Bytes).Order().ElementAt(2));
    }

    private static string PathFor(int index) => $"inner_source/source-{index:D4}.js";

    private static HttpResponseMessage Text(string content)
        => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8) };

    private sealed class SourceHandler : HttpMessageHandler
    {
        private readonly string _tree;
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;
        internal ConcurrentQueue<Uri> Requests { get; } = new();

        internal SourceHandler(int count, Func<int, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
            _tree = JsonSerializer.Serialize(new
            {
                truncated = false,
                tree = Enumerable.Range(0, count).Reverse().Select(index => new { path = PathFor(index), type = "blob" }),
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            if (request.RequestUri!.Host == "api.github.com")
            {
                if (request.RequestUri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal))
                {
                    return Task.FromResult(Text("""
                        {"sha":"fixed-commit","commit":{"tree":{"sha":"fixed-tree"}}}
                        """));
                }
                if (request.RequestUri.AbsolutePath == "/repos/example/sources")
                {
                    return Task.FromResult(Text("""{"default_branch":"main"}"""));
                }

                return Task.FromResult(Text(_tree));
            }

            var file = Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath);
            var index = int.Parse(file["source-".Length..]);
            return _respond(index, cancellationToken);
        }
    }
}
