using AniMeido.PluginProtocol;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AniMeido.PluginProtocol.Tests;

public sealed class JsonPipeRpcBoundaryTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(8)]
    public async Task Client_EofOrPartialFramePoisonsConnectionAndFutureCallsFailImmediately(int receivedBytes)
    {
        var frame = Frame(new { Id = 1, Result = 42, Error = (string?)null });
        using var stream = new ScriptedDuplexStream(frame[..receivedBytes]);
        using var client = new JsonPipeRpcClient(stream);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            client.InvokeAsync<int>("pending", []).WaitAsync(Budget));
        Assert.False(client.IsUsable);
        var writes = stream.WrittenBytes;
        var next = client.InvokeAsync<int>("after-eof", []);
        Assert.True(next.IsCompleted);
        var error = await Assert.ThrowsAsync<JsonPipeRpcException>(() => next.WaitAsync(Budget));
        Assert.Equal("IPC 连接已失效。", error.Message);
        Assert.Equal(writes, stream.WrittenBytes);
    }

    [Fact]
    public async Task Client_ResultTypeMismatchIsTerminalUnlikeBusinessError()
    {
        using var stream = new ScriptedDuplexStream(
            Frame(new { Id = 1, Result = "not-an-integer", Error = (string?)null }));
        using var client = new JsonPipeRpcClient(stream);
        await Assert.ThrowsAsync<JsonException>(() => client.InvokeAsync<int>("wrong-type", []).WaitAsync(Budget));
        Assert.False(client.IsUsable);
        await Assert.ThrowsAsync<JsonPipeRpcException>(() => client.InvokeAsync<int>("next", []).WaitAsync(Budget));

        using var businessStream = new ScriptedDuplexStream(
            Frame(new { Id = 1, Result = (int?)null, Error = "业务拒绝" })
                .Concat(Frame(new { Id = 2, Result = 17, Error = (string?)null })).ToArray());
        using var businessClient = new JsonPipeRpcClient(businessStream);
        var error = await Assert.ThrowsAsync<JsonPipeRpcException>(() => businessClient.InvokeAsync<int>("business", []));
        Assert.Equal("业务拒绝", error.Message);
        Assert.True(businessClient.IsUsable);
        Assert.Equal(17, await businessClient.InvokeAsync<int>("next", []).WaitAsync(Budget));
    }

    [Fact]
    public async Task Pipe_SequentialRequestsReturnTheirOwnResults()
    {
        await using var pair = await PipePair.OpenAsync();
        var server = new JsonPipeRpcServer(pair.Server, request =>
            Task.FromResult<object?>(request.Arguments[0].GetInt32() * 10));
        var serving = server.RunAsync();
        using var client = new JsonPipeRpcClient(pair.Client);
        for (var value = 1; value <= 12; value++)
            Assert.Equal(value * 10, await client.InvokeAsync<int>("scale", [value]).WaitAsync(Budget));
        client.Dispose();
        await serving.WaitAsync(Budget);
    }

    [Fact]
    public async Task Pipe_OverlappingRequestsAreSerializedAndResponsesStayAssociated()
    {
        await using var pair = await PipePair.OpenAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<int>();
        var server = new JsonPipeRpcServer(pair.Server, async request =>
        {
            var value = request.Arguments[0].GetInt32();
            seen.Add(request.Id);
            if (value == 1) { entered.SetResult(); await release.Task.WaitAsync(Budget); }
            return $"response-{value}";
        });
        var serving = server.RunAsync();
        using var client = new JsonPipeRpcClient(pair.Client);
        try
        {
            var first = client.InvokeAsync<string>("echo", [1]);
            await entered.Task.WaitAsync(Budget);
            var pending = Enumerable.Range(2, 12).Select(value =>
                (Value: value, Call: client.InvokeAsync<string>("echo", [value]))).ToArray();
            Assert.All(pending, item => Assert.False(item.Call.IsCompleted));
            release.TrySetResult();
            Assert.Equal("response-1", await first.WaitAsync(Budget));
            foreach (var item in pending)
                Assert.Equal($"response-{item.Value}", await item.Call.WaitAsync(Budget));
            Assert.Equal(Enumerable.Range(1, 13), seen);
        }
        finally
        {
            release.TrySetResult();
            client.Dispose();
            await serving.WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4194305)]
    public async Task Server_InvalidLengthThrowsWithoutDispatchOrResponse(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new ScriptedDuplexStream(header);
        var calls = 0;
        var server = new JsonPipeRpcServer(stream, _ => { calls++; return Task.FromResult<object?>(0); });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => server.RunAsync().WaitAsync(Budget));
        Assert.Equal("IPC 消息长度无效。", error.Message);
        Assert.Equal(0, calls);
        Assert.Equal(0, stream.WrittenBytes);
        Assert.True(stream.CanRead); // 服务端不拥有/自动关闭传入流。
    }

    [Fact]
    public async Task Server_InvalidJsonThrowsButTruncatedRequestEofReturnsNormally()
    {
        var payload = Encoding.UTF8.GetBytes("{broken");
        var bytes = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
        payload.CopyTo(bytes, 4);
        using var invalid = new ScriptedDuplexStream(bytes);
        var calls = 0;
        Task<object?> Dispatch(JsonPipeRpcRequest _) { calls++; return Task.FromResult<object?>(0); }
        await Assert.ThrowsAsync<JsonException>(() => new JsonPipeRpcServer(invalid, Dispatch).RunAsync().WaitAsync(Budget));
        using var partial = new ScriptedDuplexStream(bytes[..6]);
        await new JsonPipeRpcServer(partial, Dispatch).RunAsync().WaitAsync(Budget);
        Assert.Equal(0, calls);
        Assert.Equal(0, invalid.WrittenBytes);
        Assert.Equal(0, partial.WrittenBytes);
    }

    [Fact]
    public async Task Pipe_ServerClosesDuringPendingCallAndLaterCallFailsWithoutWriting()
    {
        await using var pair = await PipePair.OpenAsync();
        using var client = new JsonPipeRpcClient(pair.Client);
        var pending = client.InvokeAsync<int>("pending", []);
        using var budget = new CancellationTokenSource(Budget);
        var header = new byte[4];
        await pair.Server.ReadExactlyAsync(header, budget.Token);
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await pair.Server.ReadExactlyAsync(body, budget.Token);
        Assert.False(pending.IsCompleted);
        await pair.Server.DisposeAsync();
        await Assert.ThrowsAsync<EndOfStreamException>(() => pending.WaitAsync(Budget));
        Assert.False(client.IsUsable);
        var next = client.InvokeAsync<int>("later", []);
        Assert.True(next.IsCompleted);
        var error = await Assert.ThrowsAsync<JsonPipeRpcException>(() => next.WaitAsync(Budget));
        Assert.Equal("IPC 连接已失效。", error.Message);
    }
    private static byte[] Frame<T>(T value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private sealed class ScriptedDuplexStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _input = new(bytes);
        public long WrittenBytes { get; private set; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _input.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            { ct.ThrowIfCancellationRequested(); WrittenBytes += buffer.Length; return ValueTask.CompletedTask; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => WrittenBytes += count;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); /* 保留捕获输出供断连后的断言读取。 */ }
            base.Dispose(disposing);
        }
    }

    private sealed class PipePair : IAsyncDisposable
    {
        public NamedPipeServerStream Server { get; }
        public NamedPipeClientStream Client { get; }
        private PipePair(string name)
        {
            Server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        }
        public static async Task<PipePair> OpenAsync()
        {
            var pair = new PipePair($"AniMeido-Rpc-{Guid.NewGuid():N}");
            try
            {
                var accepting = pair.Server.WaitForConnectionAsync();
                await pair.Client.ConnectAsync(5000);
                await accepting.WaitAsync(Budget);
                return pair;
            }
            catch { await pair.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
