using AniMeido.PluginProtocol;
using System.Buffers.Binary;
using System.Text.Json;

namespace AniMeido.PluginProtocol.Tests;

public sealed class JsonPipeRpcTests
{
    [Fact]
    public async Task Client_BusinessErrorDoesNotPoisonConnection()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(1, 7, null);
        stream.EnqueueResponse(2, null, "业务失败");
        stream.EnqueueResponse(3, 9, null);
        using var client = new JsonPipeRpcClient(stream);

        Assert.Equal(7, await client.InvokeAsync<int>("ok", []));
        await Assert.ThrowsAsync<JsonPipeRpcException>(() =>
            client.InvokeAsync<int>("fails", []));
        Assert.True(client.IsUsable);
        Assert.Equal(9, await client.InvokeAsync<int>("ok-again", []));
        Assert.Equal(6, stream.WriteCount);
    }

    [Fact]
    public async Task Client_PreCancelledGateWaitDoesNotWriteOrPoison()
    {
        using var stream = new ScriptedDuplexStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new JsonPipeRpcClient(stream);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.InvokeAsync("cancelled", [], cancellation.Token));

        Assert.Equal(0, stream.WriteCount);
        Assert.True(client.IsUsable);
    }

    [Fact]
    public async Task Client_CancellationDuringSerializationDoesNotWriteOrPoison()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(2, 3, null);
        using var cancellation = new CancellationTokenSource();
        using var client = new JsonPipeRpcClient(stream);
        var argument = new CancelOnSerialization(cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.InvokeAsync<int>("serialize-cancel", [argument], cancellation.Token));

        Assert.Equal(0, stream.WriteCount);
        Assert.True(client.IsUsable);
        Assert.Equal(
            3,
            await client.InvokeAsync<int>("after-serialize-cancel", [])
                .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Client_GateWaitCancellationDoesNotPoisonLaterRequest()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(1, 1, null);
        stream.EnqueueResponse(2, 2, null);
        stream.BlockNextRead();
        using var client = new JsonPipeRpcClient(stream);

        var first = client.InvokeAsync<int>("first", []);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var queued = client.InvokeAsync<int>(
            "queued",
            [],
            cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queued.WaitAsync(TimeSpan.FromSeconds(5)));
        stream.ReleaseRead();

        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            2,
            await client.InvokeAsync<int>("third", [])
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(client.IsUsable);
    }

    [Fact]
    public async Task Client_PartialWriteCancellationIsTerminal()
    {
        using var stream = new ScriptedDuplexStream();
        stream.BlockNextWrite();
        using var client = new JsonPipeRpcClient(stream);
        using var cancellation = new CancellationTokenSource();

        var request = client.InvokeAsync<int>(
            "partial",
            [],
            cancellation.Token);
        await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(client.IsUsable);
        Assert.Equal(1, stream.WriteCount);
    }

    [Fact]
    public async Task Client_ReadCancellationPoisonsAndIgnoresLateBytes()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(1, 1, null);
        stream.BlockNextRead();
        using var client = new JsonPipeRpcClient(stream);
        using var cancellation = new CancellationTokenSource();

        var request = client.InvokeAsync<int>(
            "read",
            [],
            cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        stream.ReleaseRead();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            request.WaitAsync(TimeSpan.FromSeconds(5)));
        stream.EnqueueResponse(2, 2, null);
        Assert.False(client.IsUsable);
        await Assert.ThrowsAsync<JsonPipeRpcException>(() =>
            client.InvokeAsync<int>("after-read-fault", []));
    }

    [Fact]
    public async Task Client_DisposeWakesActiveAndQueuedCalls()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(1, 1, null);
        stream.BlockNextRead();
        var client = new JsonPipeRpcClient(stream);
        var active = client.InvokeAsync<int>("active", []);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = client.InvokeAsync<int>("queued", []);

        client.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            active.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queued.WaitAsync(TimeSpan.FromSeconds(5)));
        client.Dispose();
    }

    [Fact]
    public async Task Client_MismatchedResponsePoisonsConnection()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(99, 1, null);
        using var client = new JsonPipeRpcClient(stream);

        await Assert.ThrowsAsync<JsonPipeRpcException>(() =>
            client.InvokeAsync<int>("mismatch", []));
        Assert.False(client.IsUsable);
        await Assert.ThrowsAsync<JsonPipeRpcException>(() =>
            client.InvokeAsync<int>("after-fault", []));
    }

    [Fact]
    public async Task Client_InvalidFrameLengthPoisonsConnection()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueRawFrameHeader(0);
        using var client = new JsonPipeRpcClient(stream);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.InvokeAsync<int>("invalid", []));
        Assert.False(client.IsUsable);
    }

    [Fact]
    public async Task Server_DispatcherReceivesRunCancellationToken()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueRequest(1, "probe");
        using var cancellation = new CancellationTokenSource();
        CancellationToken observed = default;
        var server = new JsonPipeRpcServer(
            stream,
            (request, token) =>
            {
                _ = request;
                observed = token;
                return Task.FromResult<object?>(null);
            },
            () => true);

        await server.RunAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, observed);
        Assert.True(stream.WriteCount > 0);
    }

    [Fact]
    public async Task Server_DispatcherCancellationDoesNotWriteErrorResponse()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueRequest(1, "cancel");
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new JsonPipeRpcServer(
            stream,
            async (request, token) =>
            {
                _ = request;
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            });

        var running = server.RunAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Client_DisposeCancelsQueuedCallEvenWhenGateIsGranted()
    {
        // The queued call can be granted the gate at the same moment Dispose cancels it.
        // Repeat the scenario so that interleaving is exercised, not just the common path.
        for (var i = 0; i < 200; i++)
        {
            using var stream = new ScriptedDuplexStream();
            stream.EnqueueResponse(1, 1, null);
            stream.BlockNextRead();
            var client = new JsonPipeRpcClient(stream);
            var active = client.InvokeAsync<int>("active", []);
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = client.InvokeAsync<int>("queued", []);

            client.Dispose();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                active.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                queued.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Client_CallAfterDisposeThrowsObjectDisposed()
    {
        using var stream = new ScriptedDuplexStream();
        var client = new JsonPipeRpcClient(stream);

        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.InvokeAsync<int>("after-dispose", []));
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public void Client_DisposeIsIdempotentAndMarksConnectionUnusable()
    {
        using var stream = new ScriptedDuplexStream();
        var client = new JsonPipeRpcClient(stream);

        client.Dispose();
        client.Dispose();

        Assert.False(client.IsUsable);
    }

    private sealed class ScriptedDuplexStream : Stream
    {
        private readonly Queue<byte> _readBytes = new();
        private bool _disposed;
        private bool _blockNextRead;
        private bool _blockNextWrite;

        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource ReadReleaseSource { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource WriteReleaseSource { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int WriteCount { get; private set; }

        public void BlockNextRead() => _blockNextRead = true;

        public void ReleaseRead() => ReadReleaseSource.SetResult();

        public void BlockNextWrite() => _blockNextWrite = true;

        public void EnqueueResponse(int id, int? result, string? error)
            => EnqueueFrame(new
            {
                Id = id,
                Result = result,
                Error = error,
            });

        public void EnqueueRequest(int id, string method)
            => EnqueueFrame(new
            {
                Id = id,
                Method = method,
                Arguments = Array.Empty<JsonElement>(),
            });

        public void EnqueueRawFrameHeader(int length)
        {
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            EnqueueBytes(header);
        }

        private void EnqueueFrame<T>(T value)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value);
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            EnqueueBytes(header);
            EnqueueBytes(payload);
        }

        private void EnqueueBytes(IEnumerable<byte> bytes)
        {
            foreach (var value in bytes)
            {
                _readBytes.Enqueue(value);
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_blockNextRead)
            {
                _blockNextRead = false;
                ReadStarted.SetResult();
                await ReadReleaseSource.Task.WaitAsync(cancellationToken);
            }

            if (_readBytes.Count == 0)
            {
                return 0;
            }

            var count = Math.Min(buffer.Length, _readBytes.Count);
            for (var index = 0; index < count; index++)
            {
                buffer.Span[index] = _readBytes.Dequeue();
            }

            return count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            if (_blockNextWrite)
            {
                _blockNextWrite = false;
                WriteStarted.SetResult();
                await WriteReleaseSource.Task.WaitAsync(cancellationToken);
            }
        }

        public override void Flush() { }

        public override Task FlushAsync(
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class CancelOnSerialization
    {
        private readonly Action _cancel;

        public CancelOnSerialization(Action cancel)
        {
            _cancel = cancel;
        }

        public int Value
        {
            get
            {
                _cancel();
                return 1;
            }
        }
    }
}
