using AniMeido.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Tests;

public sealed class InstalledAppContextRegistrationTests
{
    [Fact]
    public void DefaultRegistrationResolvesOneSharedContext()
    {
        var services = new ServiceCollection().AddAppServices();
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(InstalledAppContext));
        using var provider = services.BuildServiceProvider();

        var context = provider.GetRequiredService<InstalledAppContext>();

        Assert.Same(context, provider.GetRequiredService<InstalledAppContext>());
        Assert.Same(context, Assert.Single(provider.GetServices<InstalledAppContext>()));
    }

    [Fact]
    public void ProvidedContextIsTheOnlyRegistrationAndTheSameSingletonForEveryResolution()
    {
        var context = new InstalledAppContext(null);
        var services = new ServiceCollection().AddAppServices(context);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(InstalledAppContext));
        using var provider = services.BuildServiceProvider();

        Assert.Same(context, provider.GetRequiredService<InstalledAppContext>());
        Assert.Same(context, Assert.Single(provider.GetServices<InstalledAppContext>()));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void AppPassesItsEarlyContextToCompositionInsteadOfRegisteringAnotherDescriptor()
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "AniMeido.App", "App.xaml.cs");
        var source = File.ReadAllText(path);

        Assert.Matches(@"\.AddAppServices\(\s*_installedApp\s*\)", source);
        Assert.DoesNotMatch(@"\.AddSingleton(?:<\s*InstalledAppContext\s*>)?\(\s*_installedApp\s*\)", source);
    }
}
