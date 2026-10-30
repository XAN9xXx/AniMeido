using AniMeido.App.Services;

namespace AniMeido.Tests;

public sealed class PluginManagementServiceTests : DbTestBase
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ReloadPolicyKeepsActiveWindowAndExplicitPromptSemantics(
        bool activeUi, bool forcePrompt, bool requiresConfirmation)
    {
        var host = new TestHost { HasActiveUi = activeUi };
        var service = CreateService(host);
        var confirmations = 0;

        var result = await service.ApplyPendingAsync(prompt =>
        {
            Assert.Equal(activeUi, prompt.HasActiveUi);
            Assert.Equal(requiresConfirmation, prompt.RequiresConfirmation);
            Assert.Equal(0, host.Reloads);
            confirmations++;
            return Task.FromResult(true);
        }, forcePrompt);

        Assert.True(result.Applied);
        Assert.Null(result.FailureMessage);
        Assert.Equal(1, confirmations);
        Assert.Equal(1, host.Reloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingPageOrChoosingLaterDoesNotReload(bool activeUi)
    {
        var host = new TestHost { HasActiveUi = activeUi };

        var result = await CreateService(host).ApplyPendingAsync(_ => Task.FromResult(false));

        Assert.False(result.Applied);
        Assert.Null(result.FailureMessage);
        Assert.Equal(0, host.Reloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionAndReloadFailuresAreReportedAtTheWorkflowBoundary(bool failInspection)
    {
        var host = new TestHost { FailInspection = failInspection, FailReload = !failInspection };
        var confirmations = 0;

        var result = await CreateService(host).ApplyPendingAsync(_ =>
        {
            confirmations++;
            return Task.FromResult(true);
        });

        Assert.False(result.Applied);
        Assert.Equal("synthetic host failure", result.FailureMessage);
        Assert.Equal(failInspection ? 0 : 1, confirmations);
        Assert.Equal(failInspection ? 0 : 1, host.Reloads);
    }

    [Fact]
    public async Task ManagementStateAndNotificationsUseTheExistingOwners()
    {
        var host = new TestHost();
        var service = CreateService(host);
        var notifications = 0;
        EventHandler changed = (_, _) => notifications++;
        service.StatusChanged += changed;
        host.PublishStatus("ready");

        var state = await service.GetStateAsync();

        Assert.Empty(state.Plugins);
        Assert.False(state.ReloadRequired);
        Assert.Equal("ready", state.HostStatus);
        Assert.Equal("ready", service.HostStatus);
        service.StatusChanged -= changed;
        host.PublishStatus("stopped");
        Assert.Equal(1, notifications);
    }

    private PluginManagementService CreateService(TestHost host)
        => new(new PluginPackageManager(new PluginInstallationPaths(Paths.RootDirectory),
            new PluginPackageVerifier(new Version(1, 1, 0))), host);

    private sealed class TestHost : IPluginReloadHost
    {
        public event EventHandler? StatusChanged;
        public string StatusText { get; private set; } = "initial";
        public bool HasActiveUi { get; init; }
        public bool FailInspection { get; init; }
        public bool FailReload { get; init; }
        public int Reloads { get; private set; }

        public Task<bool> HasActivePluginUiAsync(CancellationToken cancellationToken = default)
            => FailInspection
                ? Task.FromException<bool>(new IOException("synthetic host failure"))
                : Task.FromResult(HasActiveUi);

        public Task ReloadAsync(CancellationToken cancellationToken = default)
        {
            Reloads++;
            return FailReload
                ? Task.FromException(new IOException("synthetic host failure"))
                : Task.CompletedTask;
        }

        public void PublishStatus(string status)
        {
            StatusText = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
