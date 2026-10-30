using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using AniMeido.Updates;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class UpdateDownloadTimeoutTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("complete update package bytes");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoProgressForSixtySecondsStopsAndPreservesTheDownloadedPrefix(bool receivePrefix)
    {
        await using var fixture = new DownloadFixture();
        if (receivePrefix)
        {
            fixture.Body.Send(Bytes[..5]);
        }
        var downloading = fixture.Start();
        await fixture.Body.WaitForReadAsync();
        if (receivePrefix)
        {
            await fixture.Body.WaitForReadAsync();
        }

        fixture.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(downloading.IsCompleted);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<UpdateException>(() => downloading.WaitAsync(Budget));
        Assert.Equal(UpdateErrorKind.Network, error.Kind);
        Assert.Contains("60 秒", error.Message);
        Assert.Contains("已保留", error.Message);
        Assert.Equal(receivePrefix ? Bytes[..5] : [], File.ReadAllBytes(fixture.PartialPath));
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.True(fixture.Time.TimerDisposed);
    }

    [Fact]
    public async Task WaitingForTheResponseStreamAlsoUsesTheNoProgressDeadline()
    {
        await using var fixture = new DownloadFixture(delayStreamOpening: true);
        var downloading = fixture.Start();
        await fixture.Opening!.Entered.Task.WaitAsync(Budget);

        fixture.Time.Advance(TimeSpan.FromSeconds(60));

        var error = await Assert.ThrowsAsync<UpdateException>(() => downloading.WaitAsync(Budget));
        Assert.Equal(UpdateErrorKind.Network, error.Kind);
        Assert.Contains("60 秒", error.Message);
        Assert.Equal(0, new FileInfo(fixture.PartialPath).Length);
        Assert.True(fixture.Time.TimerDisposed);
    }

    [Fact]
    public async Task IdleTimeoutEndsThisCallEvenWhenTheManifestContainsMoreThanOneAddress()
    {
        await using var fixture = new DownloadFixture(includeAlternateAddress: true);
        var downloading = fixture.Start();
        await fixture.Body.WaitForReadAsync();

        fixture.Time.Advance(TimeSpan.FromSeconds(60));

        var error = await Assert.ThrowsAsync<UpdateException>(() => downloading.WaitAsync(Budget));
        Assert.Equal(UpdateErrorKind.Network, error.Kind);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task TransportIoErrorAfterTimeoutStillReportsNoProgressAndPreservesThePrefix()
    {
        await using var fixture = new DownloadFixture();
        fixture.Body.ReportCancellationAsIoError = true;
        fixture.Body.Send(Bytes[..5]);
        var downloading = fixture.Start();
        await fixture.Body.WaitForReadAsync();
        await fixture.Body.WaitForReadAsync();

        fixture.Time.Advance(TimeSpan.FromSeconds(60));

        var error = await Assert.ThrowsAsync<UpdateException>(() => downloading.WaitAsync(Budget));
        Assert.Equal(UpdateErrorKind.Network, error.Kind);
        Assert.Contains("60 秒", error.Message);
        Assert.Equal(Bytes[..5], File.ReadAllBytes(fixture.PartialPath));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task ProgressResetsTheDeadlineAndTotalDownloadTimeMayExceedSixtySeconds()
    {
        await using var fixture = new DownloadFixture();
        fixture.Body.Send(Bytes[..5]);
        var downloading = fixture.Start();
        await fixture.Body.WaitForReadAsync();
        await fixture.Body.WaitForReadAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(59));

        fixture.Body.Send(Bytes[5..10]);
        await fixture.Body.WaitForReadAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(downloading.IsCompleted);
        fixture.Body.Send(Bytes[10..]);
        fixture.Body.Send([]);

        var path = await downloading.WaitAsync(Budget);
        Assert.Equal(Bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(fixture.PartialPath));
        Assert.True(fixture.Time.TimerDisposed);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ExplicitCancellationIsNotReportedAsAnIdleTimeout()
    {
        await using var fixture = new DownloadFixture();
        fixture.Body.Send(Bytes[..5]);
        var downloading = fixture.Start();
        await fixture.Body.WaitForReadAsync();
        await fixture.Body.WaitForReadAsync();

        fixture.Cancellation.Cancel();
        fixture.Time.Advance(TimeSpan.FromSeconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloading.WaitAsync(Budget));
        Assert.Equal(Bytes[..5], File.ReadAllBytes(fixture.PartialPath));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task TimeoutStillWaitsForAnUncooperativeReadToFinish()
    {
        await using var fixture = new DownloadFixture(ignoreReadCancellation: true);
        var downloading = fixture.Start();
        await fixture.Body.WaitForReadAsync();

        fixture.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(downloading.IsCompleted);
        Assert.False(fixture.Time.TimerDisposed);
        fixture.Body.Send([]);

        var error = await Assert.ThrowsAsync<UpdateException>(() => downloading.WaitAsync(Budget));
        Assert.Equal(UpdateErrorKind.Network, error.Kind);
        Assert.Contains("60 秒", error.Message);
        Assert.True(fixture.Time.TimerDisposed);
    }

    [Fact]
    public async Task RetryingAfterTimeoutResumesTheSameAddressFromTheSavedPrefix()
    {
        await using var fixture = new DownloadFixture();
        fixture.Body.Send(Bytes[..5]);
        var downloading = fixture.Start();
        await fixture.Body.WaitForReadAsync();
        await fixture.Body.WaitForReadAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<UpdateException>(() => downloading.WaitAsync(Budget));

        var path = await fixture.Start().WaitAsync(Budget);

        Assert.Equal(Bytes, File.ReadAllBytes(path));
        Assert.Equal(2, fixture.Handler.Calls);
        Assert.False(File.Exists(fixture.PartialPath));
        Assert.True(fixture.Time.TimerDisposed);
    }

    private sealed class DownloadFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly HttpClient _client;
        private readonly UpdatePackage _package;
        private Task<string>? _download;

        internal DownloadFixture(bool ignoreReadCancellation = false,
            bool delayStreamOpening = false, bool includeAlternateAddress = false)
        {
            Body = new ControlledBody(ignoreReadCancellation);
            Opening = delayStreamOpening ? new DeferredBodyContent(Body) : null;
            var document = TestData.Document(Bytes);
            document.Package!.Urls = includeAlternateAddress
                ? ["https://r2.example.test/package.zip", "https://alternate.example.test/package.zip"]
                : ["https://r2.example.test/package.zip"];
            _package = TestData.Verified(document).Package;
            Handler = new FakeHandler((request, _) =>
            {
                Assert.Equal(_package.Urls[0], request.RequestUri);
                if (Handler!.Calls == 1)
                {
                    Assert.Null(request.Headers.Range);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = (HttpContent?)Opening ?? new StreamContent(Body),
                    });
                }

                Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
                var response = TestData.Response(Bytes[5..], HttpStatusCode.PartialContent);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, Bytes.Length - 1, Bytes.Length);
                return Task.FromResult(response);
            });
            _client = new HttpClient(Handler);
        }

        internal ControlledBody Body { get; }
        internal DeferredBodyContent? Opening { get; }
        internal ManualDownloadTime Time { get; } = new();
        internal CancellationTokenSource Cancellation { get; } = new();
        internal FakeHandler Handler { get; }
        internal string PartialPath => _directory.PathFor(_package.Sha256 + ".zip.partial");

        internal Task<string> Start()
            => _download = UpdatePackageDownloader.DownloadAsync(
                _package, _directory.Root, _client, null, Cancellation.Token, Time);

        public async ValueTask DisposeAsync()
        {
            Cancellation.Cancel();
            Opening?.Release();
            Body.Send([]);
            try
            {
                if (_download is not null)
                {
                    try
                    {
                        await _download.WaitAsync(Budget);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (UpdateException)
                    {
                    }
                }
            }
            finally
            {
                _client.Dispose();
                Body.Dispose();
                Cancellation.Dispose();
                _directory.Dispose();
            }
        }
    }

    private sealed class DeferredBodyContent(Stream body) : HttpContent
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return body;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ControlledBody(bool ignoreCancellation) : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<bool> _reads = Channel.CreateUnbounded<bool>();
        internal bool ReportCancellationAsIoError { get; set; }

        internal void Send(byte[] bytes) => _chunks.Writer.TryWrite(bytes);
        internal Task<bool> WaitForReadAsync() => _reads.Reader.ReadAsync().AsTask().WaitAsync(Budget);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reads.Writer.TryWrite(true);
            byte[] bytes;
            try
            {
                bytes = await _chunks.Reader.ReadAsync(ignoreCancellation ? default : cancellationToken);
            }
            catch (OperationCanceledException ex) when (ReportCancellationAsIoError)
            {
                throw new IOException("fixture transport closed after cancellation", ex);
            }
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualDownloadTime : TimeProvider
    {
        private readonly object _gate = new();
        private TimeSpan _elapsed;
        private ManualTimer? _timer;

        internal bool TimerDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _timer?.Disposed == true;
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                Assert.True(_timer is null || _timer.Disposed);
                _timer = new ManualTimer(this, callback, state);
                _timer.Change(dueTime, period);
                return _timer;
            }
        }

        internal void Advance(TimeSpan elapsed)
        {
            Action? fire = null;
            lock (_gate)
            {
                _elapsed += elapsed;
                if (_timer is { Disposed: false, Due: { } due } && due <= _elapsed)
                {
                    _timer.Due = null;
                    var timer = _timer;
                    fire = () => timer.Callback(timer.State);
                }
            }
            fire?.Invoke();
        }

        private sealed class ManualTimer(ManualDownloadTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal TimerCallback Callback { get; } = callback;
            internal object? State { get; } = state;
            internal bool Disposed { get; private set; }
            internal TimeSpan? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    Assert.Equal(Timeout.InfiniteTimeSpan, period);
                    if (Disposed)
                    {
                        return false;
                    }
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._elapsed + dueTime;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    Disposed = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
