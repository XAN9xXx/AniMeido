using AniMeido.App.Services;
using AniMeido.Updates;
using AniMeido.Updates.Install;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using DesktopApp = AniMeido.App.App;

namespace AniMeido.Tests;

[Collection(DevDataRootCollection.Name)]
public sealed class InstalledUpdateLoggingTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("signature")]
    [InlineData("cleanup")]
    public async Task CheckReturnsItsFailureWithoutLoggingBeforeTheFinalBoundary(string failure)
    {
        using var fixture = new InstalledUpdateServiceTests.Fixture();
        Exception? original = failure switch
        {
            "http" => new HttpRequestException("isolated offline failure"),
            "timeout" => new OperationCanceledException("isolated request timeout"),
            "cleanup" => new IOException("isolated cleanup failure"),
            "signature" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        switch (failure)
        {
            case "http":
            case "timeout":
                fixture.Response = _ => throw original!;
                break;
            case "signature":
                fixture.Signature = Encoding.ASCII.GetBytes("invalid");
                break;
            case "cleanup":
                fixture.SetStartupCleanup(Task.FromException(original!));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }

        using var logs = new CapturedLog();

        var result = await fixture.Service.CheckAsync("stable").WaitAsync(Budget);

        var expected = failure switch
        {
            "signature" => InstalledUpdateStatus.SignatureInvalid,
            "cleanup" => InstalledUpdateStatus.InvalidManifest,
            _ => InstalledUpdateStatus.NetworkError,
        };
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Manifest);
        Assert.Null(result.LatestVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        if (failure == "signature")
        {
            Assert.Equal(UpdateErrorKind.InvalidSignature, Assert.IsType<UpdateException>(result.Failure).Kind);
            Assert.DoesNotContain("2.3.0", result.FailureReason!);
            Assert.DoesNotContain("https://", result.FailureReason!);
        }
        else
        {
            Assert.Same(original, result.Failure);
        }
        Assert.Empty(logs.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulAndPendingChecksDoNotCarryAFailure(bool pending)
    {
        using var fixture = new InstalledUpdateServiceTests.Fixture();
        if (pending)
        {
            fixture.AddVersion("2.3.0");
            fixture.Store.Write(fixture.Store.Read() with { Pending = "2.3.0" });
        }
        using var logs = new CapturedLog();

        var result = await fixture.Service.CheckAsync("stable").WaitAsync(Budget);

        Assert.Equal(pending ? InstalledUpdateStatus.PendingReady : InstalledUpdateStatus.UpdateAvailable,
            result.Status);
        Assert.Equal("2.3.0", result.LatestVersion);
        Assert.Null(result.Failure);
        Assert.Null(result.FailureReason);
        Assert.Empty(logs.Events);
    }

    [Fact]
    public async Task UserCancellationPropagatesWithoutLoggingAFailure()
    {
        using var fixture = new InstalledUpdateServiceTests.Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Response = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        using var logs = new CapturedLog();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.CheckAsync("stable", cancellation.Token).WaitAsync(Budget));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Single(fixture.Requests);
        Assert.Empty(logs.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartPropagatesFailureWithoutLoggingBeforeTheCaller(bool throwWin32)
    {
        using var fixture = new InstalledUpdateServiceTests.Fixture();
        fixture.AddVersion("2.3.0");
        fixture.Store.Write(fixture.Store.Read() with { Pending = "2.3.0" });
        File.WriteAllText(Path.Combine(fixture.Root, "AniMeido.exe"), "never executed");
        var original = new Win32Exception("isolated launcher failure");
        var service = fixture.CreateService(_ => throwWin32 ? throw original : false);
        var exitRequested = false;
        using var logs = new CapturedLog();

        var error = await Record.ExceptionAsync(() =>
            service.RestartAsync(() => exitRequested = true).WaitAsync(Budget));

        if (throwWin32)
            Assert.Same(original, error);
        else
            Assert.IsType<InstallException>(error);
        Assert.False(exitRequested);
        Assert.Equal("2.3.0", fixture.Store.Read().Pending);
        Assert.Empty(logs.Events);
    }

    [Fact]
    public async Task StartupReportsTheOriginalCheckFailureOnceAsAWarning()
    {
        using var fixture = new InstalledUpdateServiceTests.Fixture();
        using var paths = new MockAppDataPaths();
        var original = new HttpRequestException("isolated startup offline failure");
        fixture.Response = _ => throw original;
        using var provider = new ServiceCollection()
            .AddSingleton(fixture.Context)
            .AddSingleton(fixture.Service)
            .AddSingleton(new DesktopSettingsStore(paths))
            .BuildServiceProvider();
        using var logs = new CapturedLog();
        var previousVersion = DesktopApp.LatestVersion;
        try
        {
            await UpdateStartupTask.CheckForUpdateSilentlyAsync(
                provider, window: null!, startupNoticesCompleted: Task.CompletedTask).WaitAsync(Budget);

            var entry = Assert.Single(logs.Events);
            Assert.Equal(LogEventLevel.Warning, entry.Level);
            Assert.Same(original, entry.Exception);
        }
        finally
        {
            DesktopApp.LatestVersion = previousVersion;
        }
    }

    private sealed class CapturedLog : IDisposable
    {
        private readonly Serilog.ILogger _previous = Log.Logger;
        private readonly EventSink _sink = new();
        private readonly Logger _logger;

        public CapturedLog()
        {
            _logger = new LoggerConfiguration().WriteTo.Sink(_sink).CreateLogger();
            Log.Logger = _logger;
        }

        public IReadOnlyCollection<LogEvent> Events => _sink.Events;

        public void Dispose()
        {
            Log.Logger = _previous;
            _logger.Dispose();
        }

        private sealed class EventSink : ILogEventSink
        {
            public ConcurrentBag<LogEvent> Events { get; } = [];

            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }
    }
}
