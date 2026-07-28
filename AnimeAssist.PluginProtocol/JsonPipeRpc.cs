using System.Buffers.Binary;
using System.Text.Json;

namespace AniMeido.PluginProtocol;

public sealed class JsonPipeRpcClient : IDisposable, IAsyncDisposable
{
    private const int MaximumMessageSize = 4 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextRequestId;
    private bool _disposed;

    public JsonPipeRpcClient(Stream stream)
        => _stream = stream;

    public async Task InvokeAsync(
        string method,
        object?[] arguments,
        CancellationToken cancellationToken = default)
        => await InvokeAsync<JsonElement>(
            method,
            arguments,
            cancellationToken);

    public async Task<T> InvokeAsync<T>(
        string method,
        object?[] arguments,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var request = new JsonPipeRpcRequest(
                Interlocked.Increment(ref _nextRequestId),
                method,
                arguments.Select(argument =>
                    JsonSerializer.SerializeToElement(argument)).ToArray());
            await JsonPipeRpcFraming.WriteAsync(
                _stream,
                request,
                cancellationToken);
            var response = await JsonPipeRpcFraming.ReadAsync<JsonPipeRpcResponse>(
                _stream,
                MaximumMessageSize,
                cancellationToken);
            if (response.Id != request.Id)
            {
                throw new JsonPipeRpcException("IPC 响应 ID 不匹配。");
            }

            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                throw new JsonPipeRpcException(response.Error);
            }

            if (typeof(T) == typeof(JsonElement)
                && response.Result is null)
            {
                return (T)(object)default(JsonElement);
            }

            if (response.Result is not JsonElement result)
            {
                throw new JsonPipeRpcException("IPC 响应缺少结果。");
            }

            var value = result.Deserialize<T>();
            return value is not null
                ? value
                : throw new JsonPipeRpcException("IPC 响应结果为空。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        _gate.Dispose();
    }
}

public sealed class JsonPipeRpcServer
{
    private const int MaximumMessageSize = 4 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly Func<JsonPipeRpcRequest, Task<object?>> _dispatcher;
    private readonly Func<bool>? _shouldStopAfterResponse;

    public JsonPipeRpcServer(
        Stream stream,
        Func<JsonPipeRpcRequest, Task<object?>> dispatcher,
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
                var result = await _dispatcher(request);
                response = new JsonPipeRpcResponse(
                    request.Id,
                    result is null
                        ? null
                        : JsonSerializer.SerializeToElement(result),
                    null);
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
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
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
