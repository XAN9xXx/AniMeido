using AniMeido.PluginProtocol;
using System.Text.Json;

namespace AniMeido.PluginProtocol.Tests;

public sealed partial class JsonPipeRpcBoundaryTests
{
    [Fact]
    public void Exception_LegacyConstructorPreservesMessageAndDefaultsToLocalOrigin()
    {
        var error = new JsonPipeRpcException("legacy failure");

        Assert.Equal("legacy failure", error.Message);
        Assert.False(error.IsRemoteError);
        Assert.False(typeof(JsonPipeRpcException).GetProperty(nameof(JsonPipeRpcException.IsRemoteError))!.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exception_ExplicitOriginPreservesTheGivenValue(bool isRemoteError)
    {
        var error = new JsonPipeRpcException("explicit failure", isRemoteError);

        Assert.Equal("explicit failure", error.Message);
        Assert.Equal(isRemoteError, error.IsRemoteError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipe_RemoteBusinessErrorHasRemoteOriginAndKeepsTheConnectionUsable(bool nullable)
    {
        await using var pair = await PipePair.OpenAsync();
        var original = new InvalidOperationException("fixture business failure");
        var reported = new List<(string Method, Exception Error)>();
        var server = new JsonPipeRpcServer(pair.Server,
            request => request.Method == "fails"
                ? Task.FromException<object?>(original)
                : Task.FromResult<object?>(17),
            reportDispatchFailure: (method, error) => reported.Add((method, error)));
        var serving = server.RunAsync();
        using var client = new JsonPipeRpcClient(pair.Client);
        var calls = new List<Task>();
        Exception? testFailure = null;
        try
        {
            Task failed = nullable
                ? client.InvokeNullableAsync<string>("fails", [])
                : client.InvokeAsync<int>("fails", []);
            calls.Add(failed);
            var error = await Assert.ThrowsAsync<JsonPipeRpcException>(() => failed.WaitAsync(Budget));

            Assert.True(error.IsRemoteError);
            Assert.Equal(original.Message, error.Message);
            Assert.True(client.IsUsable);
            var report = Assert.Single(reported);
            Assert.Equal("fails", report.Method);
            Assert.Same(original, report.Error);

            var next = client.InvokeAsync<int>("next", []);
            calls.Add(next);
            Assert.Equal(17, await next.WaitAsync(Budget));
            client.Dispose();
            await serving.WaitAsync(Budget);
        }
#pragma warning disable CA1031 // 保留主失败，再由共享收尾等待真实管道任务。
        catch (Exception ex)
        {
            testFailure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            await FinishPipeTestAsync(client, serving, calls, testFailure);
        }
    }

    [Theory]
    [InlineData("id", "IPC 响应 ID 不匹配。")]
    [InlineData("missing", "IPC 响应缺少结果。")]
    [InlineData("null", "IPC 响应缺少结果。")]
    public async Task Pipe_LocalResponseFaultAndLaterTerminalFaultKeepLocalOrigin(string fault, string message)
    {
        await using var pair = await PipePair.OpenAsync();
        async Task RespondAsync()
        {
            using var budget = new CancellationTokenSource(Budget);
            var request = await JsonPipeRpcFraming.ReadAsync<JsonPipeRpcRequest>(
                pair.Server, JsonPipeRpcFraming.MaximumMessageSize, budget.Token);
            object response = fault switch
            {
                "id" => new { Id = request.Id + 1, Result = "value", Error = (string?)null },
                "missing" => new { request.Id, Error = (string?)null },
                _ => new { request.Id, Result = (string?)null, Error = (string?)null },
            };
            await JsonPipeRpcFraming.WriteAsync(pair.Server, response, budget.Token);
        }

        var serving = RespondAsync();
        using var client = new JsonPipeRpcClient(pair.Client);
        var calls = new List<Task>();
        Exception? testFailure = null;
        try
        {
            var failed = client.InvokeAsync<string>("invalid-response", []);
            calls.Add(failed);
            var error = await Assert.ThrowsAsync<JsonPipeRpcException>(() => failed.WaitAsync(Budget));

            Assert.False(error.IsRemoteError);
            Assert.Equal(message, error.Message);
            Assert.False(client.IsUsable);

            var next = client.InvokeAsync<int>("after-terminal-fault", []);
            calls.Add(next);
            Assert.True(next.IsCompleted);
            var terminal = await Assert.ThrowsAsync<JsonPipeRpcException>(() => next.WaitAsync(Budget));
            Assert.False(terminal.IsRemoteError);
            Assert.Equal("IPC 连接已失效。", terminal.Message);
            await serving.WaitAsync(Budget);
        }
#pragma warning disable CA1031 // 保留主失败，再由共享收尾等待真实管道任务。
        catch (Exception ex)
        {
            testFailure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            await FinishPipeTestAsync(client, serving, calls, testFailure);
        }
    }

    [Fact]
    public async Task Pipe_ServerBusinessErrorRetainsTheExistingThreeResponseFields()
    {
        await using var pair = await PipePair.OpenAsync();
        var server = new JsonPipeRpcServer(pair.Server,
            _ => Task.FromException<object?>(new InvalidOperationException("wire failure")));
        var serving = server.RunAsync();
        using var clientOwner = new JsonPipeRpcClient(pair.Client);
        var calls = new List<Task>();
        Exception? testFailure = null;
        try
        {
            using var budget = new CancellationTokenSource(Budget);
            var write = JsonPipeRpcFraming.WriteAsync(pair.Client,
                new { Id = 41, Method = "fails", Arguments = Array.Empty<int>() }, budget.Token);
            calls.Add(write);
            await write.WaitAsync(Budget);
            var read = JsonPipeRpcFraming.ReadAsync<JsonElement>(
                pair.Client, JsonPipeRpcFraming.MaximumMessageSize, budget.Token);
            calls.Add(read);
            var response = await read.WaitAsync(Budget);

            Assert.Equal(new[] { "Error", "Id", "Result" },
                response.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal(41, response.GetProperty("Id").GetInt32());
            Assert.Equal("wire failure", response.GetProperty("Error").GetString());
            Assert.Equal(JsonValueKind.Null, response.GetProperty("Result").ValueKind);
            clientOwner.Dispose();
            await serving.WaitAsync(Budget);
        }
#pragma warning disable CA1031 // 保留主失败，再由共享收尾等待真实管道任务。
        catch (Exception ex)
        {
            testFailure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            await FinishPipeTestAsync(clientOwner, serving, calls, testFailure);
        }
    }
}
