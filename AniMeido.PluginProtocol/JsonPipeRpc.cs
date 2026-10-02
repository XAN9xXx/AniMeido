using System.Buffers.Binary;
using System.Text.Json;

namespace AniMeido.PluginProtocol;

public sealed class JsonPipeRpcClient : IDisposable, IAsyncDisposable
{
    private const int MaximumMessageSize = 4 * 1024 * 1024;
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

    public JsonPipeRpcClient(Stream stream)
    {
        _stream = stream;
        _lifetimeToken = _lifetimeCancellation.Token;
    }

    public bool IsUsable
        => Volatile.Read(ref _terminalFault) == 0
            && Volatile.Read(ref _disposed) == 0;

    public async Task InvokeAsync(
        string method,
        object?[] arguments,
        CancellationToken cancellationToken = default)
        => await InvokeAsync<JsonElement>(
            method,
            arguments,
            cancellationToken);

    public async Task<T?> InvokeNullableAsync<T>(
        string method,
        object?[] arguments,
        CancellationToken cancellationToken = default) where T : class
        => await InvokeCoreAsync<T>(method, arguments, cancellationToken, allowNull: true);

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
            var request = new JsonPipeRpcRequest(
                Interlocked.Increment(ref _nextRequestId),
                method,
                arguments.Select(argument =>
                    JsonSerializer.SerializeToElement(argument)).ToArray());
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
                    MaximumMessageSize,
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
                throw new JsonPipeRpcException(response.Error);
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

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

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

public sealed class JsonPipeRpcServer
{
    private const int MaximumMessageSize = 4 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly Func<
        JsonPipeRpcRequest,
        CancellationToken,
        Task<object?>> _dispatcher;
    private readonly Func<bool>? _shouldStopAfterResponse;

    public JsonPipeRpcServer(
        Stream stream,
        Func<JsonPipeRpcRequest, Task<object?>> dispatcher,
        Func<bool>? shouldStopAfterResponse = null)
        : this(
            stream,
            (request, _) => dispatcher(request),
            shouldStopAfterResponse)
    {
    }

    public JsonPipeRpcServer(
        Stream stream,
        Func<JsonPipeRpcRequest, CancellationToken, Task<object?>> dispatcher,
        Func<bool>? shouldStopAfterResponse = null)
    {
        _stream = stream;
        _dispatcher = dispatcher;
        _shouldStopAfterResponse = shouldStopAfterResponse;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            JsonPipeRpcRequest request;
            try
            {
                request = await JsonPipeRpcFraming.ReadAsync<JsonPipeRpcRequest>(
                    _stream,
                    MaximumMessageSize,
                    cancellationToken);
            }
            catch (EndOfStreamException)
            {
                return;
            }

            JsonPipeRpcResponse response;
            try
            {
                var result = await _dispatcher(request, cancellationToken);
                response = new JsonPipeRpcResponse(
                    request.Id,
                    result is null
                        ? null
                        : JsonSerializer.SerializeToElement(result),
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
                response = new JsonPipeRpcResponse(
                    request.Id,
                    null,
                    ex.Message);
            }
#pragma warning restore CA1031
            await JsonPipeRpcFraming.WriteAsync(
                _stream,
                response,
                cancellationToken);
            if (_shouldStopAfterResponse?.Invoke() == true)
            {
                return;
            }
        }
    }
}

public sealed class JsonPipeRpcException : Exception
{
    public JsonPipeRpcException(string message)
        : base(message)
    {
    }
}

public sealed record JsonPipeRpcRequest(
    int Id,
    string Method,
    JsonElement[] Arguments);

internal sealed record JsonPipeRpcResponse(
    int Id,
    JsonElement? Result,
    string? Error);

internal static class JsonPipeRpcFraming
{
    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken,
        Action? onWriteStarted = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        cancellationToken.ThrowIfCancellationRequested();
        onWriteStarted?.Invoke();
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
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
