using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Tests;

public sealed class DatabaseStartupTests : DbTestBase
{
    [Fact]
    public async Task Initialization_ResolvesAndRunsDatabaseOutsideCallerContext()
    {
        var context = new SynchronizationContext();
        var services = new ServiceCollection();
        services.AddSingleton(_ =>
        {
            Assert.NotSame(context, SynchronizationContext.Current);
            return new DatabaseService(DbFactory, Paths);
        });
        await using var provider = services.BuildServiceProvider();
        var previous = SynchronizationContext.Current;
        Task initialization;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            initialization = DatabaseStartup.InitializeAsync(provider);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await initialization;

        using var connection = await DbFactory.OpenAsync();
        Assert.Equal(PersonalDatabaseSchema.CurrentVersion,
            await PersonalDatabaseSchema.GetSchemaVersionAsync(connection));
    }
}
