using AniMeido.App.Services;
using AniMeido.PluginProtocol;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class PluginHostSupervisorRestartTests
{
    [Theory]
    [InlineData(false, "io")]
    [InlineData(false, "timeout")]
    [InlineData(false, "json")]
    [InlineData(false, "rpc")]
    [InlineData(true, "io")]
    [InlineData(true, "timeout")]
    [InlineData(true, "json")]
    [InlineData(true, "rpc")]
    public async Task Activation_LocalFailureIsLoggedOnceAndPropagatesTheOriginalException(bool settings, string kind)
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = Assert.Single(fixture.Sessions);
        Exception failure = kind switch
        {
            "io" => new IOException("fixture connection failed"),
            "timeout" => new TimeoutException("fixture response timed out"),
            "json" => new JsonException("fixture response malformed"),
            _ => new JsonPipeRpcException("fixture response identity mismatch"),
        };
        SetActivationRequest(session, settings, (_, _) => Task.FromException(failure));

        var actual = await Record.ExceptionAsync(() => ActivateAsync(supervisor, session.PluginId, settings).WaitAsync(Budget));

        Assert.Same(failure, actual);
        var entry = Assert.Single(fixture.Logger.Warnings);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Contains(session.PluginId, entry.Message, StringComparison.Ordinal);
        Assert.Contains(ActivationId(settings), entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activation_RemoteFailurePropagatesWithoutAnotherClientLog(bool settings)
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = Assert.Single(fixture.Sessions);
        var failure = new JsonPipeRpcException("fixture remote failure", isRemoteError: true);
        SetActivationRequest(session, settings, (_, _) => Task.FromException(failure));

        var actual = await Record.ExceptionAsync(() => ActivateAsync(supervisor, session.PluginId, settings).WaitAsync(Budget));

        Assert.Same(failure, actual);
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activation_RequestCancellationPropagatesWithoutAnError(bool settings)
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = Assert.Single(fixture.Sessions);
        var failure = new OperationCanceledException("fixture RPC lifetime ended");
        SetActivationRequest(session, settings, (_, _) => Task.FromException(failure));

        var actual = await Record.ExceptionAsync(() => ActivateAsync(supervisor, session.PluginId, settings).WaitAsync(Budget));

        Assert.Same(failure, actual);
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activation_StartupFailureIsLoggedOnceAndPropagatesTheOriginalException(bool settings)
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = Assert.Single(fixture.Sessions);
        var failure = new InvalidOperationException("fixture session activation failed");
        session.NextStart = _ => Task.FromException<PluginHostSnapshot>(failure);

        var actual = await Record.ExceptionAsync(() => ActivateAsync(supervisor, session.PluginId, settings).WaitAsync(Budget));

        Assert.Same(failure, actual);
        var entry = Assert.Single(fixture.Logger.Warnings);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Contains(session.PluginId, entry.Message, StringComparison.Ordinal);
        Assert.Contains(ActivationId(settings), entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activation_StoppingCancelsAnInFlightStartupWithoutAnError(bool settings)
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = Assert.Single(fixture.Sessions);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PluginHostSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.NextStart = token =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(token);
        };
        var activation = ActivateAsync(supervisor, session.PluginId, settings);
        try
        {
            await entered.Task.WaitAsync(Budget);
            var stopping = supervisor.StopAndDrainAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation.WaitAsync(Budget));
            await stopping.WaitAsync(Budget);
            Assert.Empty(fixture.Logger.Warnings);
        }
        finally
        {
            release.TrySetResult(new PluginHostSnapshot([], [], [], []));
            await supervisor.StopAndDrainAsync().WaitAsync(Budget);
            try
            {
                await activation.WaitAsync(Budget);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activation_SuccessInvokesTheOriginalTargetOnceWithoutAnError(bool settings)
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = Assert.Single(fixture.Sessions);
        var requests = new List<string>();
        SetActivationRequest(session, settings, (id, _) =>
        {
            requests.Add(id);
            return Task.CompletedTask;
        });

        await ActivateAsync(supervisor, session.PluginId, settings).WaitAsync(Budget);

        Assert.Equal(ActivationId(settings), Assert.Single(requests));
        Assert.Empty(fixture.Logger.Warnings);
    }

    private static string ActivationId(bool settings)
        => settings ? "fixture.plugin.settings" : "fixture.plugin.open";

    private static Task ActivateAsync(PluginHostSupervisor supervisor, string pluginId, bool settings)
        => settings
            ? supervisor.OpenSettingsAsync(pluginId, ActivationId(settings))
            : supervisor.InvokeCommandAsync(pluginId, ActivationId(settings));

    private static void SetActivationRequest(
        FakeSession session,
        bool settings,
        Func<string, CancellationToken, Task> request)
    {
        if (settings)
            session.SettingsRequest = request;
        else
            session.CommandRequest = request;
    }
}
