using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed class PersonalAnimeDataSourceSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public enum ReadKind
    {
        LocalDetail,
        DeactivatedDetailWithoutTitle,
        Studios,
        Tags,
        VoiceActors,
        Characters,
    }

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO legacy_anime_refs(LocalId, LegacyBangumiId, Reason, TitleSnapshot, RecordedAt)
            VALUES(-42, 42, 'not_found', '保留的本地标题', '2026-10-27');
            INSERT INTO public_id_issues(AnimeId, Kind, TitleSnapshot, DetectedAt)
            VALUES(42, 'deactivated', NULL, '2026-10-27');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(ReadKind.LocalDetail)]
    [InlineData(ReadKind.DeactivatedDetailWithoutTitle)]
    [InlineData(ReadKind.Studios)]
    [InlineData(ReadKind.Tags)]
    [InlineData(ReadKind.VoiceActors)]
    [InlineData(ReadKind.Characters)]
    [Trait("Category", "Mechanism")]
    public async Task LocalRead_ReturnsWhileSqliteIsLocked_AndCompletesWithoutTheCallingContextOrHttp(ReadKind kind)
    {
        using var handler = new RejectHttpHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid") };
        var source = Source(client);
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync<Task>(() => ReadAsync(source, kind), context);
        Task result;

        try
        {
            result = await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(result.IsCompleted);
            blocker.Dispose();
            await result.WaitAsync(CompletionBudget);
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            var pending = await invocation.WaitAsync(CompletionBudget);
            await pending.WaitAsync(CompletionBudget);
        }

        switch (kind)
        {
            case ReadKind.LocalDetail:
            case ReadKind.DeactivatedDetailWithoutTitle:
                var anime = Assert.IsType<UnavailableAnime>(await (Task<Anime?>)result);
                Assert.Equal(kind == ReadKind.LocalDetail ? -42 : 42, anime.ID);
                Assert.Equal(kind == ReadKind.LocalDetail ? "保留的本地标题" : AnimeTitleFallback.Untitled, anime.Title);
                Assert.Null(anime.CoverURL);
                break;
            case ReadKind.Studios:
                Assert.Empty(await (Task<List<Studio>>)result);
                break;
            case ReadKind.Tags:
                Assert.Empty(await (Task<List<Tag>>)result);
                break;
            case ReadKind.VoiceActors:
                Assert.Empty(await (Task<List<VoiceActor>>)result);
                break;
            case ReadKind.Characters:
                Assert.Empty(await (Task<List<CharacterRole>>)result);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(ReadKind.LocalDetail)]
    [InlineData(ReadKind.Tags)]
    public async Task LocalRead_PreservesCancellationAndPropagatesRealSqliteFailures(ReadKind kind)
    {
        using var handler = new RejectHttpHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid") };
        var source = Source(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(source, kind, cancellation.Token));

        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            var table = kind == ReadKind.LocalDetail ? "legacy_anime_refs" : "public_id_issues";
            command.CommandText = $"DROP TABLE {table}";
            await command.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<SqliteException>(() => ReadAsync(source, kind));
        Assert.Equal(1, error.SqliteErrorCode);
        Assert.Equal(0, handler.RequestCount);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task ZeroDetailAndNegativeAuxiliaryReads_ReturnWithoutDatabaseOrHttpEvenWhenCancelled()
    {
        using var handler = new RejectHttpHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid") };
        var source = Source(client);
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE legacy_anime_refs; DROP TABLE public_id_issues;";
            await command.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Null(await source.GetAnimeDetailAsync(0, cancellation.Token));
        Assert.Empty(await source.GetStudioAsync(-42, cancellation.Token));
        Assert.Empty(await source.GetTagsAsync(-42, cancellation.Token));
        Assert.Empty(await source.GetCVsAsync(-42, cancellation.Token));
        Assert.Empty(await source.GetCharacterRolesAsync(-42, cancellation.Token));
        Assert.Equal(0, handler.RequestCount);
    }

    private PersonalAnimeDataSource Source(HttpClient client)
        => new(new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, client, new CacheService(Factory)), Factory);

    private static Task ReadAsync(PersonalAnimeDataSource source, ReadKind kind, CancellationToken ct = default)
        => kind switch
        {
            ReadKind.LocalDetail => source.GetAnimeDetailAsync(-42, ct),
            ReadKind.DeactivatedDetailWithoutTitle => source.GetAnimeDetailAsync(42, ct),
            ReadKind.Studios => source.GetStudioAsync(42, ct),
            ReadKind.Tags => source.GetTagsAsync(42, ct),
            ReadKind.VoiceActors => source.GetCVsAsync(42, ct),
            ReadKind.Characters => source.GetCharacterRolesAsync(42, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private sealed class RejectHttpHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            throw new InvalidOperationException("本地保留与停用资料的读取不应访问远端。");
        }
    }
}
