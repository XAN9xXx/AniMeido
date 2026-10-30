using AniMeido.Plugin.Player.Diagnostics;
using System.IO.Compression;

namespace AniMeido.Tests;

public sealed class PlaybackDiagnosticRecorderTests
{
    [Fact]
    public async Task Recorder_RedactsSecretsAndExportsZip()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"animeido-diagnostics-{Guid.NewGuid():N}");
        var exportPath = Path.Combine(
            Path.GetTempPath(),
            $"animeido-diagnostics-{Guid.NewGuid():N}.zip");
        try
        {
            await using var recorder =
                new PlaybackDiagnosticRecorder(root);
            await recorder.StartAsync(CancellationToken.None);
            recorder.Record(
                "http",
                "response",
                "source-a",
                new Uri(
                    "https://media.test/video.m3u8?token=uri-secret"),
                new Dictionary<string, object?>
                {
                    ["Cookie"] = "session=data-secret",
                    ["status"] = 200,
                    ["redirect"] =
                        "https://media.test/next?signature=field-secret",
                },
                """
                <input name="csrf" value="body-secret">
                https://media.test/play?token=body-url-secret
                """);
            await recorder.ExportAsync(
                exportPath,
                CancellationToken.None);

            Assert.True(File.Exists(exportPath));
            using var archive = ZipFile.OpenRead(exportPath);
            var eventsEntry = Assert.Single(
                archive.Entries,
                entry => entry.FullName == "events.jsonl");
            using var reader = new StreamReader(eventsEntry.Open());
            var content = await reader.ReadToEndAsync();

            Assert.DoesNotContain("uri-secret", content);
            Assert.DoesNotContain("data-secret", content);
            Assert.DoesNotContain("field-secret", content);
            Assert.DoesNotContain("body-secret", content);
            Assert.DoesNotContain("body-url-secret", content);
            Assert.Contains("redacted", content);
            Assert.Contains("\"status\":200", content);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (File.Exists(exportPath))
            {
                File.Delete(exportPath);
            }
        }
    }

    [Fact]
    public void SanitizeResponseSnippet_TruncatesLargeBodies()
    {
        var sanitized =
            PlaybackDiagnosticRecorder.SanitizeResponseSnippet(
                new string(
                    'x',
                    PlaybackDiagnosticRecorder.MaximumSnippetLength + 10));

        Assert.Equal(
            PlaybackDiagnosticRecorder.MaximumSnippetLength,
            sanitized.Length);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("clear")]
    [InlineData("start")]
    [InlineData("export")]
    [InlineData("dispose")]
    public async Task LaterOperationsWaitForTheWriterAlreadyBeingStopped(
        string operation)
    {
        await using var fixture = await BlockedRecorder.CreateAsync();
        var stopping = fixture.Track(
            fixture.Recorder.StopAsync(CancellationToken.None));
        Assert.False(stopping.IsCompleted);

        var pending = fixture.Track(operation switch
        {
            "stop" => fixture.Recorder.StopAsync(CancellationToken.None),
            "clear" => fixture.Recorder.ClearAsync(CancellationToken.None),
            "start" => fixture.Recorder.StartAsync(CancellationToken.None),
            "export" => fixture.Recorder.ExportAsync(
                fixture.ExportPath, CancellationToken.None),
            "dispose" => fixture.Recorder.DisposeAsync().AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });

        Assert.False(pending.IsCompleted);
        fixture.ReleaseWriter();
        await Task.WhenAll(stopping, pending).WaitAsync(BlockedRecorder.Timeout);
    }

    [Fact]
    public async Task CancelingOneStopWaitKeepsTheWriterOwnedByOtherWaiters()
    {
        await using var fixture = await BlockedRecorder.CreateAsync();
        var stopping = fixture.Track(
            fixture.Recorder.StopAsync(CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        var canceledWait = fixture.Track(
            fixture.Recorder.StopAsync(cancellation.Token));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledWait.WaitAsync(BlockedRecorder.Timeout));
        Assert.False(stopping.IsCompleted);

        var clearing = fixture.Track(
            fixture.Recorder.ClearAsync(CancellationToken.None));
        Assert.False(clearing.IsCompleted);
        fixture.ReleaseWriter();
        await Task.WhenAll(stopping, clearing).WaitAsync(BlockedRecorder.Timeout);
        Assert.False(Directory.Exists(fixture.Root));

        await fixture.StartAnotherBlockedSessionAsync();
        using var activeCancellation = new CancellationTokenSource();
        var canceledStop = fixture.Track(
            fixture.Recorder.StopAsync(activeCancellation.Token));
        Assert.False(fixture.Recorder.IsEnabled);
        Assert.False(canceledStop.IsCompleted);

        activeCancellation.Cancel();
        var resumedStop = fixture.Track(
            fixture.Recorder.StopAsync(CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledStop.WaitAsync(BlockedRecorder.Timeout));

        var resumedClear = fixture.Track(
            fixture.Recorder.ClearAsync(CancellationToken.None));
        Assert.False(resumedStop.IsCompleted);
        Assert.False(resumedClear.IsCompleted);
        fixture.ReleaseWriter();
        await Task.WhenAll(resumedStop, resumedClear).WaitAsync(BlockedRecorder.Timeout);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task StartingDuringClearWaitsAndKeepsTheNewSessionFiles()
    {
        await using var fixture = await BlockedRecorder.CreateAsync();
        var originalDirectory = fixture.Recorder.LastSessionDirectory;
        var clearing = fixture.Track(
            fixture.Recorder.ClearAsync(CancellationToken.None));
        var starting = fixture.Track(
            fixture.Recorder.StartAsync(CancellationToken.None));

        Assert.False(clearing.IsCompleted);
        Assert.False(starting.IsCompleted);
        fixture.ReleaseWriter();
        await Task.WhenAll(clearing, starting).WaitAsync(BlockedRecorder.Timeout);

        var newDirectory = Assert.IsType<string>(
            fixture.Recorder.LastSessionDirectory);
        Assert.NotEqual(originalDirectory, newDirectory);
        Assert.Equal(
            newDirectory,
            Assert.Single(Directory.GetDirectories(fixture.Root)));
        fixture.Recorder.Record("test", "new-session-survived-clear");
        await fixture.Track(fixture.Recorder.StopAsync(CancellationToken.None))
            .WaitAsync(BlockedRecorder.Timeout);

        var events = await File.ReadAllTextAsync(
            Path.Combine(newDirectory, "events.jsonl"));
        Assert.Contains("new-session-survived-clear", events);
        Assert.DoesNotContain("blocked-entry", events);
    }

    [Fact]
    public async Task DisposeSharesItsDrainAndRejectsNewOperations()
    {
        await using var fixture = await BlockedRecorder.CreateAsync();
        var disposing = fixture.Track(fixture.Recorder.DisposeAsync().AsTask());
        var repeatedDispose = fixture.Track(fixture.Recorder.DisposeAsync().AsTask());

        Assert.Same(disposing, repeatedDispose);
        Assert.False(disposing.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Track(fixture.Recorder.StartAsync(CancellationToken.None))
                .WaitAsync(BlockedRecorder.Timeout));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Track(fixture.Recorder.ExportAsync(
                    fixture.ExportPath, CancellationToken.None))
                .WaitAsync(BlockedRecorder.Timeout));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Track(fixture.Recorder.ClearAsync(CancellationToken.None))
                .WaitAsync(BlockedRecorder.Timeout));

        var stopping = fixture.Track(
            fixture.Recorder.StopAsync(CancellationToken.None));
        Assert.False(stopping.IsCompleted);
        fixture.ReleaseWriter();
        await Task.WhenAll(disposing, repeatedDispose, stopping)
            .WaitAsync(BlockedRecorder.Timeout);
        await fixture.Recorder.StopAsync(CancellationToken.None)
            .WaitAsync(BlockedRecorder.Timeout);
        Assert.Same(disposing, fixture.Recorder.DisposeAsync().AsTask());
    }

    private sealed class BlockedRecorder : IAsyncDisposable
    {
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private BlockingDiagnosticValue _value = new();
        private readonly List<Task> _operations = [];

        private BlockedRecorder()
        {
            Root = Path.Combine(
                Path.GetTempPath(), $"animeido-diagnostic-drain-{Guid.NewGuid():N}");
            Recorder = new PlaybackDiagnosticRecorder(Root);
        }

        public string Root { get; }

        public string ExportPath => Path.Combine(Root, "export.zip");

        public PlaybackDiagnosticRecorder Recorder { get; }

        public static async Task<BlockedRecorder> CreateAsync()
        {
            var fixture = new BlockedRecorder();
            try
            {
                await fixture.StartBlockedSessionAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public Task Track(Task operation)
        {
            _operations.Add(operation);
            return operation;
        }

        public void ReleaseWriter() => _value.Release();

        public async Task StartAnotherBlockedSessionAsync()
        {
            _value.Dispose();
            _value = new BlockingDiagnosticValue();
            await StartBlockedSessionAsync();
        }

        private async Task StartBlockedSessionAsync()
        {
            await Track(Task.Run(() => Recorder.StartAsync(CancellationToken.None)))
                .WaitAsync(Timeout);
            Recorder.Record(
                "test", "blocked-write",
                data: new Dictionary<string, object?> { ["payload"] = _value });
            await _value.Entered.WaitAsync(Timeout);
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseWriter();
            var disposing = Recorder.DisposeAsync().AsTask();
            await Task.WhenAll(_operations.Append(disposing).Select(ObserveCompletionAsync))
                .WaitAsync(Timeout);
            _value.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static async Task ObserveCompletionAsync(Task operation)
        {
            try
            {
                await operation;
            }
#pragma warning disable CA1031 // Assertions own operation results; cleanup still observes every task.
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        }
    }

    private sealed class BlockingDiagnosticValue : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();

        internal Task Entered => _entered.Task;

        public string Value
        {
            get
            {
                _entered.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("The diagnostic writer was not released.");
                }

                return "blocked-entry";
            }
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }
}
