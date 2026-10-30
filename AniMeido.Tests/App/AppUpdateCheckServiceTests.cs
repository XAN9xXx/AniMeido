using System.Net;
using AniMeido.App.Services;
using AniMeido.Updates.Install;

namespace AniMeido.Tests;

public sealed class AppUpdateCheckServiceTests : DbTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task LegacyCheckDoesNotReadInstalledSettingsAndStartsOffTheCallingContext()
    {
        Directory.CreateDirectory(Path.Combine(Paths.RootDirectory, "desktop-settings.json"));
        using var handler = new CallbackHandler((request, _) =>
        {
            Assert.Null(SynchronizationContext.Current);
            Assert.Equal("https://example.invalid/version.json", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"latestVersion":"2.3.0","downloadUrl":"https://example.invalid/app.exe"}
                    """),
            });
        });
        var service = CreateService(new InstalledAppContext(null), handler);

        var result = Assert.IsType<AppUpdateCheckResult.Legacy>(await service.CheckAsync().WaitAsync(Budget));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Result.Status);
        Assert.Equal("2.3.0", result.LatestVersion);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("preview")]
    public async Task InstalledCheckUsesTheSavedChannelAndNeverCallsLegacyHttp(string channel)
    {
        var root = Path.Combine(Paths.RootDirectory, "install");
        var executable = Path.Combine(InstallLayout.VersionDirectory(root, "2.2.0"), InstallLayout.AppFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllTextAsync(executable, "isolated test executable; never launched");
        new InstallStateStore(root).Write(new InstallState { Current = "2.2.0" });
        var context = new InstalledAppContext(executable);
        var settings = new DesktopSettingsStore(Paths);
        await settings.UpdateAsync(current => current with { UpdateChannel = channel });
        var requests = new List<string>();
        using var handler = new CallbackHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            throw new HttpRequestException("isolated offline response");
        });
        var service = CreateService(context, handler);

        var result = Assert.IsType<AppUpdateCheckResult.Installed>(await service.CheckAsync().WaitAsync(Budget));

        Assert.Equal(InstalledUpdateStatus.NetworkError, result.Result.Status);
        Assert.Equal($"https://dl.animeido.com/updates/win-x64/{channel}/release.json", Assert.Single(requests));
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task LeavingThePageCanCancelLegacyHttpWithoutReturningAFailureResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CallbackHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        var service = CreateService(new InstalledAppContext(null), handler);
        using var cancellation = new CancellationTokenSource();
        var check = service.CheckAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Budget);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.WaitAsync(Budget));
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAny(check).WaitAsync(Budget);
            await ((Task)check).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task PreCanceledCheckDoesNotRequestHttpOrReadSettings()
    {
        using var handler = new CallbackHandler((_, _) =>
            throw new InvalidOperationException("No request expected"));
        var service = CreateService(new InstalledAppContext(null), handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cancellation.Token));
    }

    private AppUpdateCheckService CreateService(InstalledAppContext context, HttpMessageHandler handler)
    {
        var factory = new ClientFactory(handler);
        return new AppUpdateCheckService(context, new DesktopSettingsStore(Paths),
            new InstalledUpdateService(factory, context),
            new UpdateService(factory, "https://example.invalid/version.json", new Version(2, 2, 0)));
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
