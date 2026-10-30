using AniMeido.App.Services;
using AniMeido.PluginProtocol;
using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

public sealed partial class PluginHostSupervisorRestartTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackContextProvider_ActualReadFailureReturnsNullAndWarnsOnce(bool timeout)
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        Exception failure = timeout
            ? new TimeoutException("Synthetic playback context timeout.")
            : new InvalidOperationException("Synthetic playback context failure.");
        var session = Assert.Single(fixture.Sessions);
        session.ActiveContextRequest = _ => Task.FromException<HostedActivePlaybackContext?>(failure);
        var logger = new PlaybackContextLogger();
        var provider = new HostedActivePlaybackContextProvider(supervisor, logger);

        var result = await provider.GetActiveContextAsync().WaitAsync(Budget);

        Assert.Null(result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public async Task PlaybackContextProvider_NoPlaybackCapabilityAndNullContextDoesNotWarn()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        Assert.True(Assert.Single(fixture.Sessions).IsRunning);
        var logger = new PlaybackContextLogger();
        var provider = new HostedActivePlaybackContextProvider(supervisor, logger);

        var result = await provider.GetActiveContextAsync().WaitAsync(Budget);

        Assert.Null(result);
        Assert.Empty(logger.Entries);
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public async Task PlaybackContextProvider_NoRunningHostReturnsNullWithoutWarning()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = Assert.Single(fixture.Sessions);
        await StatusAsync(supervisor, "Fixture 已关闭，用到时会自动启动", () => session.Exit(0));
        var logger = new PlaybackContextLogger();
        var provider = new HostedActivePlaybackContextProvider(supervisor, logger);

        var result = await provider.GetActiveContextAsync().WaitAsync(Budget);

        Assert.Null(result);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task PlaybackContextProvider_CancellationPropagatesWithoutWarning()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = new OperationCanceledException("Synthetic playback context cancellation.", cancellation.Token);
        Assert.Single(fixture.Sessions).ActiveContextRequest = token =>
        {
            Assert.Equal(cancellation.Token, token);
            return Task.FromException<HostedActivePlaybackContext?>(failure);
        };
        var logger = new PlaybackContextLogger();
        var provider = new HostedActivePlaybackContextProvider(supervisor, logger);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetActiveContextAsync(cancellation.Token).WaitAsync(Budget));

        Assert.Same(failure, actual);
        Assert.Empty(logger.Entries);
        Assert.Empty(fixture.Logger.Warnings);
    }

    private sealed class PlaybackContextLogger : ILogger<HostedActivePlaybackContextProvider>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
    }
}
