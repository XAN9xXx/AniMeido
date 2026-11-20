using AniMeido.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Tests;

[Collection(AniMeido.Desktop.Tests.UnoHeadlessTestHost.CollectionName)]
public sealed class InstalledAppContextRegistrationTests
{
    [Fact]
    public Task DefaultRegistrationResolvesOneSharedContext()
        => AniMeido.Desktop.Tests.UnoHeadlessTestHost.RunAsync(testRoot =>
        {
            var services = new ServiceCollection().AddAppServices();
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(InstalledAppContext));
            using var provider = services.BuildServiceProvider();

            var context = provider.GetRequiredService<InstalledAppContext>();

            Assert.Same(context, provider.GetRequiredService<InstalledAppContext>());
            Assert.Same(context, Assert.Single(provider.GetServices<InstalledAppContext>()));
            return Task.CompletedTask;
        });

    [Fact]
    public Task ProvidedContextIsTheOnlyRegistrationAndTheSameSingletonForEveryResolution()
        => AniMeido.Desktop.Tests.UnoHeadlessTestHost.RunAsync(testRoot =>
        {
            var context = new InstalledAppContext(null);
            var services = new ServiceCollection().AddAppServices(context);
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(InstalledAppContext));
            using var provider = services.BuildServiceProvider();

            Assert.Same(context, provider.GetRequiredService<InstalledAppContext>());
            Assert.Same(context, Assert.Single(provider.GetServices<InstalledAppContext>()));
            return Task.CompletedTask;
        });

    [Fact]
    [Trait("Category", "Mechanism")]
    public void AppPassesItsEarlyContextToCompositionInsteadOfRegisteringAnotherDescriptor()
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "AniMeido.Desktop", "App.xaml.cs");
        var source = File.ReadAllText(path);

        Assert.Matches(@"\.AddAppServices\(\s*_installedApp\s*\)", source);
        Assert.DoesNotMatch(@"\.AddSingleton(?:<\s*InstalledAppContext\s*>)?\(\s*_installedApp\s*\)", source);
    }
}
