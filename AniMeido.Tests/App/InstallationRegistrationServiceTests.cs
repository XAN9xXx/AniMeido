using AniMeido.App.Services;
using AniMeido.Updates.Install;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Tests;

public sealed class InstallationRegistrationServiceTests
{
    [Fact]
    public void AppRegistrationResolvesMigrationServicesUsingInjectedRegistry()
    {
        using var paths = new MockAppDataPaths();
        var context = LegacyInstallationServiceTests.CreateContext(paths, true);
        var registry = new FakeInstallationRegistry();
        using var provider = new ServiceCollection()
            .AddAppServices()
            .AddSingleton(context)
            .AddSingleton<IInstallationRegistry>(registry)
            .BuildServiceProvider();
        Assert.Empty(provider.GetRequiredService<LegacyInstallationService>().Detect());
        provider.GetRequiredService<InstallationRegistrationService>().UpdateDisplayVersion();
        Assert.Equal("3.0.0", registry.WrittenVersion);
        Assert.NotEmpty(typeof(InstallationRegistry).GetConstructors());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UpdatesOnlyExistingInstalledRegistration(bool installed, bool keyExists)
    {
        using var paths = new MockAppDataPaths();
        var registry = new FakeInstallationRegistry { KeyExists = keyExists };
        var context = LegacyInstallationServiceTests.CreateContext(paths, installed);
        new InstallationRegistrationService(context, registry).UpdateDisplayVersion();
        Assert.Equal(installed && keyExists ? "3.0.0" : null, registry.WrittenVersion);
        Assert.Empty(registry.Reads);
    }

    [Fact]
    public void WriteFailureDoesNotUndoHealthyState()
    {
        using var paths = new MockAppDataPaths();
        var context = LegacyInstallationServiceTests.CreateContext(paths, true);
        context.MarkHealthy();
        new InstallationRegistrationService(context, new FakeInstallationRegistry { FailWrite = true }).UpdateDisplayVersion();
        Assert.True(new InstallStateStore(context.InstallRoot!).Read().CurrentHealthy);
    }

    [Fact]
    public void AppIdIsDifferentFromLegacyAndNamesThePerUserUninstallKey()
    {
        Assert.NotEqual(InstallationIdentity.LegacyAppId, InstallationIdentity.AppId);
        Assert.True(Guid.TryParse(InstallationIdentity.AppId, out _));
        Assert.EndsWith(InstallationIdentity.AppId + "_is1", InstallationIdentity.UninstallKey);
    }
}
