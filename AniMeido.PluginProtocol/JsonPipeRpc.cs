using System.Buffers.Binary;
using System.Text.Json;

namespace AniMeido.PluginProtocol;

/// <summary>在单条双向流上串行完成请求与响应的 JSON RPC 客户端。</summary>
/// <remarks>
/// <para>客户端拥有传入流，关闭或连接失效时会关闭流。并发调用共享连接，不交错读写各自的响应。</para>
/// <para>写帧步骤开始前取消不会使连接失效；开始后取消会终止连接，但不能保证远端尚未执行操作。</para>
/// <para>远端业务错误不会自动终止连接；断帧、响应 ID 不符或无效结果会使连接失效。</para>
/// </remarks>
public sealed class JsonPipeRpcClient : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    // These synchronization objects intentionally remain undisposed until GC. No WaitHandle is
    // acquired, and a late WaitAsync continuation may still call Release after Dispose/Poison.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Keep a stable lifetime token for linked operations; disposing the CTS would race token
    // registration for an in-flight operation, so cancellation and stream close are sufficient.
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private int _nextRequestId;
    private int _disposed;
    private int _terminalFault;

    /// <summary>接管用于请求和响应的双向流。</summary>
    /// <param name="stream">由客户端负责关闭的流。</param>
    public JsonPipeRpcClient(Stream stream)
    {
        _stream = stream;
        _lifetimeToken = _lifetimeCancellation.Token;
    }

    /// <summary>客户端尚未关闭，且尚未判定连接失效。</summary>
    /// <remarks>这是本地已知状态，不会向远端发起健康检查。</remarks>
    public bool IsUsable
        => Volatile.Read(ref _terminalFault) == 0
            && Volatile.Read(ref _disposed) == 0;

    /// <summary>调用 RPC 方法，不向调用方返回业务结果。</summary>
    /// <remarks>仍会等待并校验响应；取消遵循客户端的连接生命周期规则。</remarks>
    public async Task InvokeAsync(
        string method,
        object?[] arguments,
        CancellationToken cancellationToken = default)
        => await InvokeAsync<JsonElement>(
            method,
            arguments,
            cancellationToken);

    /// <summary>调用允许空引用结果的 RPC 方法。</summary>
    /// <remarks>缺少结果字段或结果为 JSON null 时返回 null，不因此使连接失效；业务错误仍会抛出。</remarks>
    public async Task<T?> InvokeNullableAsync<T>(
        string method,
        object?[] arguments,
        CancellationToken cancellationToken = default) where T : class
        => await InvokeCoreAsync<T>(method, arguments, cancellationToken, allowNull: true);

    /// <summary>调用 RPC 方法并反序列化必需的结果。</summary>
    /// <remarks>需要可空引用结果时使用 <see cref="InvokeNullableAsync{T}"/>。</remarks>
    public async Task<T> InvokeAsync<T>(
        string method,
        object?[] arguments,
        CancellationToken cancellationToken = default)
        => (await InvokeCoreAsync<T>(method, arguments, cancellationToken, allowNull: false))!;

    private async Task<T?> InvokeCoreAsync<T>(
        string method,
        object?[] arguments,
        CancellationToken cancellationToken,
        bool allowNull)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ThrowIfTerminalFault();
        var entered = false;
        var frameWriteStarted = false;
        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeToken,
                cancellationToken);
        var operationToken = operationCancellation.Token;
        try
        {
            await _gate.WaitAsync(operationToken);
            entered = true;
            if (Volatile.Read(ref _disposed) != 0)
            {
                // SemaphoreSlim can still grant the gate to a waiter whose token is being
                // cancelled when Release wins the race (Dispose cancels the lifetime token while
                // the active call releases the gate). Report the cancellation that woke this
                // queued call; calls started after Dispose still fail with ObjectDisposedException.
                throw new OperationCanceledException(_lifetimeToken);
            }

            ThrowIfTerminalFault();
            var request = new OutgoingJsonPipeRpcRequest(
                Interlocked.Increment(ref _nextRequestId),
                method,
                arguments);
            await JsonPipeRpcFraming.WriteAsync(
                _stream,
                request,
                operationToken,
                () => frameWriteStarted = true);
            JsonPipeRpcResponse response;
            try
            {
                response = await JsonPipeRpcFraming.ReadAsync<JsonPipeRpcResponse>(
                    _stream,
                    JsonPipeRpcFraming.MaximumMessageSize,
                    operationToken);
            }
            catch (Exception ex) when (
                ex is OperationCanceledException
                    or EndOfStreamException
                    or InvalidDataException
                    or JsonException
                    or IOException)
            {
                Poison();
                throw;
            }
            if (response.Id != request.Id)
            {
                var error = new JsonPipeRpcException("IPC 响应 ID 不匹配。");
                Poison();
                throw error;
            }

            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                throw new JsonPipeRpcException(response.Error, isRemoteError: true);
            }

            if (allowNull && (response.Result is null
                || response.Result.Value.ValueKind == JsonValueKind.Null))
            {
                return default;
            }

            if (typeof(T) == typeof(JsonElement)
                && response.Result is null)
            {
                return (T)(object)default(JsonElement);
            }

            if (response.Result is not JsonElement result)
            {
                var error = new JsonPipeRpcException("IPC 响应缺少结果。");
                Poison();
                throw error;
            }

            try
            {
                var value = result.Deserialize<T>();
                return value is not null
                    ? value
                    : throw new JsonPipeRpcException("IPC 响应结果为空。");
            }
            catch (JsonPipeRpcException)
            {
                Poison();
                throw;
            }
            catch (JsonException)
            {
                Poison();
                throw;
            }
        }
        catch (OperationCanceledException)
            when (frameWriteStarted)
        {
            if (frameWriteStarted)
            {
                Poison();
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancellation before frame writing leaves this connection usable.
            throw;
        }
        catch (IOException)
        {
            if (frameWriteStarted)
                Poison();
            throw;
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    /// <summary>执行与 <see cref="Dispose"/> 相同的关闭操作。</summary>
    /// <remarks>当前同步关闭流并发出取消，不等待所有活动调用返回。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>取消活动和排队调用，并关闭所拥有的流；重复调用无效。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        try
        {
            _stream.Dispose();
        }
#pragma warning disable CA1031 // Disposal must still wake waiters if a stream misbehaves.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Dispose must wake in-flight I/O and remain idempotent.
        }
    }

    private void ThrowIfTerminalFault()
    {
        if (Volatile.Read(ref _terminalFault) != 0)
        {
            throw new JsonPipeRpcException("IPC 连接已失效。");
        }
    }

    private void Poison()
    {
        if (Interlocked.Exchange(ref _terminalFault, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        try
        {
            _stream.Dispose();
        }
#pragma warning disable CA1031 // Preserve the original terminal protocol failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Preserve the original protocol or I/O failure.
        }
    }
}

/// <summary>借用双向流，逐个分派请求并返回响应的 JSON RPC 服务端。</summary>
/// <remarks>
/// <para>流由调用方关闭和释放。每帧使用 4 字节小端长度头和 UTF-8 JSON 正文，正文上限为 4 MiB，不含帧头。</para>
/// <para>分派失败转换为错误响应；无效帧长度或 JSON 错误向调用方传播。超大业务结果可返回错误后继续下一请求。</para>
/// </remarks>
public sealed class JsonPipeRpcServer
{
    private readonly Stream _stream;
    private readonly Func<
        JsonPipeRpcRequest,
        CancellationToken,
        Task<object?>> _dispatcher;
    private readonly Func<bool>? _shouldStopAfterResponse;
    private readonly Action<string, Exception>? _reportDispatchFailure;

    /// <summary>使用不接收取消令牌的便利分派入口；运行令牌仍控制流的读写。</summary>
    public JsonPipeRpcServer(
        Stream stream,
        Func<JsonPipeRpcRequest, Task<object?>> dispatcher,
        Func<bool>? shouldStopAfterResponse = null,
        Action<string, Exception>? reportDispatchFailure = null)
        : this(
            stream,
            (request, _) => dispatcher(request),
            shouldStopAfterResponse,
            reportDispatchFailure)
    {
    }

    /// <summary>使用接收运行取消令牌的分派入口。</summary>
    /// <param name="stream">借用的双向流，由调用方负责关闭和释放。</param>
    /// <param name="dispatcher">处理一条请求的业务入口。</param>
    /// <param name="shouldStopAfterResponse">响应写完后检查；返回 true 时结束循环。</param>
    /// <param name="reportDispatchFailure">接收原始分派异常供宿主诊断，不将异常堆栈写入响应。</param>
    public JsonPipeRpcServer(
        Stream stream,
        Func<JsonPipeRpcRequest, CancellationToken, Task<object?>> dispatcher,
        Func<bool>? shouldStopAfterResponse = null,
        Action<string, Exception>? reportDispatchFailure = null)
    {
        _stream = stream;
        _dispatcher = dispatcher;
        _shouldStopAfterResponse = shouldStopAfterResponse;
        _reportDispatchFailure = reportDispatchFailure;
    }

    /// <summary>处理请求直到流结束、响应后的停止条件满足，或运行被取消。</summary>
    /// <remarks>取消控制读写，并传给支持令牌的分派函数；分派中的运行取消不写业务错误响应。</remarks>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            JsonPipeRpcRequest request;
            try
            {
                request = await JsonPipeRpcFraming.ReadAsync<JsonPipeRpcRequest>(
                    _stream,
                    JsonPipeRpcFraming.MaximumMessageSize,
                    cancellationToken);
            }
            catch (EndOfStreamException)
            {
                return;
            }

            OutgoingJsonPipeRpcResponse response;
            try
            {
                var result = await _dispatcher(request, cancellationToken);
                response = new OutgoingJsonPipeRpcResponse(
                    request.Id,
                    result,
                    null);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // RPC must return structured plugin failures to the App.
            catch (Exception ex)
            {
                _reportDispatchFailure?.Invoke(request.Method, ex);
                response = new OutgoingJsonPipeRpcResponse(
                    request.Id,
                    null,
                    ex.Message);
            }
#pragma warning restore CA1031
            var writeStarted = false;
            try
            {
                await JsonPipeRpcFraming.WriteAsync(
                    _stream, response, cancellationToken, () => writeStarted = true);
            }
            catch (InvalidDataException ex) when (!writeStarted)
            {
                // Too-large results are business failures; no partial frame has been sent.
                await JsonPipeRpcFraming.WriteAsync(
                    _stream,
                    new OutgoingJsonPipeRpcResponse(request.Id, null, ex.Message),
                    cancellationToken);
            }
            if (_shouldStopAfterResponse?.Invoke() == true)
            {
                return;
            }
        }
    }
}

/// <summary>远端业务错误，或客户端识别的协议及连接失效错误。</summary>
/// <remarks>此异常不必然使连接失效；是否仍可调用由 <see cref="JsonPipeRpcClient.IsUsable"/> 表达。</remarks>
public sealed class JsonPipeRpcException : Exception
{
    /// <summary>创建客户端本机识别的协议或连接错误。</summary>
    public JsonPipeRpcException(string message)
        : this(message, isRemoteError: false)
    {
    }

    /// <summary>创建错误，并标明它是否来自远端业务响应。</summary>
    public JsonPipeRpcException(string message, bool isRemoteError)
        : base(message)
    {
        IsRemoteError = isRemoteError;
    }

    /// <summary>远端以 Error 响应返回的业务错误为 true。</summary>
    public bool IsRemoteError { get; }
}

/// <summary>反序列化后的请求；公开成员名也是默认 JSON 传输字段名。</summary>
/// <param name="Id">用于关联请求与响应的 ID，不是业务去重标识。</param>
/// <param name="Method">稳定的方法路由名。</param>
/// <param name="Arguments">按业务方法参数位置排列的 JSON 值。</param>
public sealed record JsonPipeRpcRequest(
    int Id,
    string Method,
    JsonElement[] Arguments);

internal sealed record JsonPipeRpcResponse(
    int Id,
    JsonElement? Result,
    string? Error);

internal sealed record OutgoingJsonPipeRpcRequest(int Id, string Method, object?[] Arguments);
internal sealed record OutgoingJsonPipeRpcResponse(int Id, object? Result, string? Error);

/// <summary>既有帧格式：4 字节小端正文长度，后接不超过 4 MiB 的 UTF-8 JSON 正文。</summary>
/// <remarks>写入前完成有界序列化；超限请求不会先向管道写出部分帧。</remarks>
internal static class JsonPipeRpcFraming
{
    internal const int MaximumMessageSize = 4 * 1024 * 1024;

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken,
        Action? onWriteStarted = null)
    {
        using var payload = new BoundedPayloadStream();
        await JsonSerializer.SerializeAsync(payload, value, cancellationToken: cancellationToken);
        var length = checked((int)payload.Length);
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        cancellationToken.ThrowIfCancellationRequested();
        onWriteStarted?.Invoke();
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload.GetBuffer().AsMemory(0, length), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        int maximumMessageSize,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumMessageSize)
        {
            throw new InvalidDataException("IPC 消息长度无效。");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload)
            ?? throw new InvalidDataException("IPC 消息 JSON 无效。");
    }

    private sealed class BoundedPayloadStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > MaximumMessageSize)
                throw new InvalidDataException("IPC 消息超过 4 MiB 上限。");

            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > MaximumMessageSize)
                throw new InvalidDataException("IPC 消息超过 4 MiB 上限。");

            base.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer[offset..],
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}
