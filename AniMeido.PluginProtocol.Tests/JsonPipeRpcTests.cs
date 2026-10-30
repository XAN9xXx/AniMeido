using AniMeido.PluginProtocol;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace AniMeido.PluginProtocol.Tests;

public sealed class JsonPipeRpcTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_NullableNullDoesNotPoisonConnection(bool omitResult)
    {
        using var stream = new ScriptedDuplexStream();
        if (omitResult)
            stream.EnqueueFrame(new { Id = 1, Error = (string?)null });
        else
            stream.EnqueueResponse(1, null, null);
        stream.EnqueueResponse(2, 9, null);
        using var client = new JsonPipeRpcClient(stream);

        Assert.Null(await client.InvokeNullableAsync<string>("no-context", []));
        Assert.True(client.IsUsable);
        Assert.Equal(9, await client.InvokeAsync<int>("next", []));
    }

    [Fact]
    public async Task Client_NullableValueIsDeserialized()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueFrame(new { Id = 1, Result = new { Name = "playing" }, Error = (string?)null });
        using var client = new JsonPipeRpcClient(stream);

        var value = await client.InvokeNullableAsync<NullableResult>("context", []);

        Assert.NotNull(value);
        Assert.Equal("playing", value.Name);
        Assert.True(client.IsUsable);
    }

    [Fact]
    public async Task Client_NullableBusinessErrorDoesNotPoisonConnection()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(1, null, "业务失败");
        stream.EnqueueResponse(2, 9, null);
        using var client = new JsonPipeRpcClient(stream);

        var error = await Assert.ThrowsAsync<JsonPipeRpcException>(() =>
            client.InvokeNullableAsync<string>("fails", []));

        Assert.Equal("业务失败", error.Message);
        Assert.True(client.IsUsable);
        Assert.Equal(9, await client.InvokeAsync<int>("next", []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_RequiredNullStillPoisonsConnection(bool omitResult)
    {
        using var stream = new ScriptedDuplexStream();
        if (omitResult)
            stream.EnqueueFrame(new { Id = 1, Error = (string?)null });
        else
            stream.EnqueueResponse(1, null, null);
        using var client = new JsonPipeRpcClient(stream);

        await Assert.ThrowsAsync<JsonPipeRpcException>(() => client.InvokeAsync<string>("required", []));

        Assert.False(client.IsUsable);
        await Assert.ThrowsAsync<JsonPipeRpcException>(() => client.InvokeAsync<int>("next", []));
    }

    [Fact]
    public async Task Client_NullableInvalidResultPoisonsConnection()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(1, 7, null);
        using var client = new JsonPipeRpcClient(stream);

        await Assert.ThrowsAsync<JsonException>(() => client.InvokeNullableAsync<NullableResult>("invalid", []));

        Assert.False(client.IsUsable);
    }

    [Fact]
    public async Task Framing_ExactLimitIsAcceptedAndLargerPayloadWritesNothing()
    {
        using var stream = new MemoryStream();
        var value = new string('a', JsonPipeRpcFraming.MaximumMessageSize - 2);
        await JsonPipeRpcFraming.WriteAsync(stream, value, CancellationToken.None);
        Assert.Equal(JsonPipeRpcFraming.MaximumMessageSize + sizeof(int), stream.Length);

        stream.SetLength(0);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            JsonPipeRpcFraming.WriteAsync(stream, value + "b", CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Client_OversizedArgumentsDoNotWriteOrPoison()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(2, 9, null);
        using var client = new JsonPipeRpcClient(stream);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.InvokeAsync<int>("large", [new string('x', JsonPipeRpcFraming.MaximumMessageSize)]));

        Assert.Equal(0, stream.WriteCount);
        Assert.True(client.IsUsable);
        Assert.Equal(9, await client.InvokeAsync<int>("next", []));
    }

    [Fact]
    public async Task Server_OversizedResultReturnsErrorAndKeepsNextRequestUsable()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueRequest(1, "large");
        stream.EnqueueRequest(2, "next");
        var server = new JsonPipeRpcServer(stream, request => Task.FromResult<object?>(
            request.Id == 1 ? new string('x', JsonPipeRpcFraming.MaximumMessageSize) : 9));
        await server.RunAsync();

        stream.Written.Position = 0;
        var first = await JsonPipeRpcFraming.ReadAsync<JsonPipeRpcResponse>(
            stream.Written, JsonPipeRpcFraming.MaximumMessageSize, CancellationToken.None);
        var second = await JsonPipeRpcFraming.ReadAsync<JsonPipeRpcResponse>(
            stream.Written, JsonPipeRpcFraming.MaximumMessageSize, CancellationToken.None);
        Assert.Equal(1, first.Id);
        Assert.Contains("4 MiB", first.Error);
        Assert.Null(first.Result);
        Assert.Equal(2, second.Id);
        Assert.Equal(9, second.Result!.Value.GetInt32());
    }

    [Fact]
    public async Task Server_ReportsOriginalDispatchExceptionOnce()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueRequest(1, "fails");
        var error = new InvalidOperationException("operation failed");
        var observed = new List<(string Method, Exception Error)>();
        var server = new JsonPipeRpcServer(
            stream,
            _ => Task.FromException<object?>(error),
            reportDispatchFailure: (method, failure) => observed.Add((method, failure)));
        await server.RunAsync();

        var report = Assert.Single(observed);
        Assert.Equal("fails", report.Method);
        Assert.Same(error, report.Error);
        Assert.NotNull(report.Error.StackTrace);
        stream.Written.Position = 0;
        var response = await JsonPipeRpcFraming.ReadAsync<JsonPipeRpcResponse>(
            stream.Written, JsonPipeRpcFraming.MaximumMessageSize, CancellationToken.None);
        Assert.Equal(error.Message, response.Error);
    }

    private sealed record NullableResult(string Name);

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

        using var cancellation = new CancellationTokenSource();
        var calls = new List<Task>();
        Exception? testFailure = null;
        try
        {
            var first = client.InvokeAsync<int>("first", []);
            calls.Add(first);
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = client.InvokeAsync<int>("queued", [], cancellation.Token);
            calls.Add(queued);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                queued.WaitAsync(TimeSpan.FromSeconds(5)));
            stream.ReleaseRead();

            Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
            var third = client.InvokeAsync<int>("third", []);
            calls.Add(third);
            Assert.Equal(2, await third.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(client.IsUsable);
        }
#pragma warning disable CA1031 // 主失败保留到真实调用排空后再交还。
        catch (Exception ex)
        {
            testFailure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            stream.ReleaseRead();
            await FinishRpcTestAsync(client.Dispose, calls, testFailure);
        }
    }

    [Fact]
    public async Task Client_PartialWriteCancellationIsTerminal()
    {
        using var stream = new ScriptedDuplexStream();
        stream.BlockNextWrite();
        using var client = new JsonPipeRpcClient(stream);
        using var cancellation = new CancellationTokenSource();

        var calls = new List<Task>();
        Exception? testFailure = null;
        try
        {
            var request = client.InvokeAsync<int>("partial", [], cancellation.Token);
            calls.Add(request);
            await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                request.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(client.IsUsable);
            Assert.Equal(1, stream.WriteCount);
        }
#pragma warning disable CA1031 // 主失败保留到真实调用排空后再交还。
        catch (Exception ex)
        {
            testFailure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            await FinishRpcTestAsync(client.Dispose, calls, testFailure);
        }
    }

    [Fact]
    public async Task Client_ReadCancellationPoisonsAndIgnoresLateBytes()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(1, 1, null);
        stream.BlockNextRead();
        using var client = new JsonPipeRpcClient(stream);
        using var cancellation = new CancellationTokenSource();

        var calls = new List<Task>();
        Exception? testFailure = null;
        try
        {
            var request = client.InvokeAsync<int>("read", [], cancellation.Token);
            calls.Add(request);
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            stream.ReleaseRead();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                request.WaitAsync(TimeSpan.FromSeconds(5)));
            stream.EnqueueResponse(2, 2, null);
            Assert.False(client.IsUsable);
            var afterFault = client.InvokeAsync<int>("after-read-fault", []);
            calls.Add(afterFault);
            await Assert.ThrowsAsync<JsonPipeRpcException>(() => afterFault);
        }
#pragma warning disable CA1031 // 主失败保留到真实调用排空后再交还。
        catch (Exception ex)
        {
            testFailure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            stream.ReleaseRead();
            await FinishRpcTestAsync(client.Dispose, calls, testFailure);
        }
    }

    [Fact]
    public async Task Client_DisposeWakesActiveAndQueuedCalls()
    {
        using var stream = new ScriptedDuplexStream();
        stream.EnqueueResponse(1, 1, null);
        stream.BlockNextRead();
        var client = new JsonPipeRpcClient(stream);
        var calls = new List<Task>();
        Exception? testFailure = null;
        try
        {
            var active = client.InvokeAsync<int>("active", []);
            calls.Add(active);
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = client.InvokeAsync<int>("queued", []);
            calls.Add(queued);

            client.Dispose();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                active.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                queued.WaitAsync(TimeSpan.FromSeconds(5)));
            client.Dispose();
        }
#pragma warning disable CA1031 // 主失败保留到真实调用排空后再交还。
        catch (Exception ex)
        {
            testFailure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            await FinishRpcTestAsync(client.Dispose, calls, testFailure);
        }
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
        Exception? testFailure = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, stream.WriteCount);
        }
#pragma warning disable CA1031 // 主失败保留到服务端取消并排空后再交还。
        catch (Exception ex)
        {
            testFailure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            await FinishRpcTestAsync(cancellation.Cancel, [running], testFailure);
        }
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
            var calls = new List<Task>();
            Exception? testFailure = null;
            try
            {
                var active = client.InvokeAsync<int>("active", []);
                calls.Add(active);
                await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var queued = client.InvokeAsync<int>("queued", []);
                calls.Add(queued);

                client.Dispose();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    active.WaitAsync(TimeSpan.FromSeconds(5)));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    queued.WaitAsync(TimeSpan.FromSeconds(5)));
            }
#pragma warning disable CA1031 // 每一轮都交还资源并观察实际调用，再报告主失败。
            catch (Exception ex)
            {
                testFailure = ex;
            }
#pragma warning restore CA1031
            finally
            {
                await FinishRpcTestAsync(client.Dispose, calls, testFailure);
            }
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

    private static async Task FinishRpcTestAsync(
        Action stop,
        IReadOnlyList<Task> operations,
        Exception? testFailure)
    {
        var failures = new List<Exception>();
        if (testFailure is not null)
            failures.Add(testFailure);

        var stopFailure = Record.Exception(stop);
        if (stopFailure is not null)
            failures.Add(stopFailure);

        var drainFailure = await Record.ExceptionAsync(() =>
            ObserveCompletionAsync(Task.WhenAll(operations)).WaitAsync(TimeSpan.FromSeconds(5)));
        if (drainFailure is not null)
            failures.Add(drainFailure);

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("The RPC test and its cleanup did not both complete successfully.", failures);
    }

    private static async Task ObserveCompletionAsync(Task completion)
        => await completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private sealed class ScriptedDuplexStream : Stream
    {
        public MemoryStream Written { get; } = new();

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

        public void ReleaseRead() => ReadReleaseSource.TrySetResult();

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

        public void EnqueueFrame<T>(T value)
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
            await Written.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() { }

        public override Task FlushAsync(
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            if (disposing)
                Written.Dispose();
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
