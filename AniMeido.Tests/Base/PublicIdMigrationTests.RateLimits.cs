using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using AniMeido.Contracts;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class PublicIdMigrationTests
{
    [Fact]
    public async Task FinalRateLimitPreservesEveryLegacyWorkRecordAndMigrationVersion()
    {
        await CreateV8Async();
        await SeedAllTablesAsync(1);
        using var handler = new RateLimitHandler();
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<MetadataServiceException>(() => Service(client).MigrateAsync());
        Assert.True(error.IsRateLimited);
        Assert.Equal(PersonalDatabaseSchema.OfflineVersion, await VersionAsync());
        foreach (var table in PublicIdMigrationService.WorkTables)
            Assert.True(await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE AnimeId=1") > 0, table);
        Assert.False(File.Exists(Path.Combine(Paths.RootDirectory, "id-migration-v9.json")));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FinalRateLimitLeavesPublicIdCheckRecordsAndCompletionMarkerUnchanged()
    {
        await new AniMeido.App.Services.DatabaseService(DbFactory, Paths).InitializeAsync();
        await ExecuteAsync("INSERT INTO tracking VALUES(42,1,@at)");
        using var handler = new RateLimitHandler();
        using var client = Client(handler);
        var cache = new CacheService(DbFactory);
        var metadata = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, client, cache);
        var remap = new PersonalIdRemapService(DbFactory, NullLogger<PersonalIdRemapService>.Instance, cache);
        await using var check = new PublicIdCheckService(DbFactory, client, metadata, remap, NullLogger<PublicIdCheckService>.Instance);
        var error = await Assert.ThrowsAsync<MetadataServiceException>(() => check.CheckAsync("20261030.1"));
        Assert.True(error.IsRateLimited);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM tracking WHERE AnimeId=42"));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM config WHERE Key='public-id-check-version'"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task RegistrationSharesOneServiceBudgetAcrossMetadataChecksAndMigration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppDataPaths>(Paths);
        services.AddSingleton(DbFactory);
        services.AddMetadataService();
        await using var provider = services.BuildServiceProvider();
        var requests = provider.GetRequiredService<PublicServiceRequests>();
        foreach (var consumer in new object[]
        {
            provider.GetRequiredService<MetadataDataSource>(),
            provider.GetRequiredService<PublicIdCheckService>(),
            provider.GetRequiredService<PublicIdMigrationService>(),
        })
            Assert.Same(requests, consumer.GetType().GetField("_requests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(consumer));
    }

    private sealed class RateLimitHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60)) },
            });
        }
    }
}
