using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AniMeido.Tests;

public sealed partial class MetadataDataSourceTests
{
    [Fact]
    public async Task TagVocabularyFailureOnlyAnnouncesTheRetryThatActuallyHappens()
    {
        await CreateBaseTablesAsync();
        var vocabRequests = 0;
        using var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/works/42")
                return Raw(DetailJson);

            return Vocab(request, $"changing-{++vocabRequests}");
        });
        using var client = Client(handler);
        var log = new MetadataLogCapture();
        var source = new MetadataDataSource(log, client, new CacheService(DbFactory), new TestTime());

        var error = await Assert.ThrowsAsync<MetadataServiceException>(
            () => source.GetTagsAsync(42, default));

        Assert.Equal("读取标签词表期间资料版本变化，请重试。", error.Message);
        Assert.Equal(8, vocabRequests);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("retrying", entry.Message);
        Assert.Null(entry.Error);
    }

    [Fact]
    public async Task TagVocabularyRecoveryAnnouncesOneRetryAndKeepsTheNewVersion()
    {
        await CreateBaseTablesAsync();
        var version = Version1;
        using var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/works/42")
                return Raw(DetailJson);

            var category = Query(request, "category");
            if (category == "format")
                version = Version2;
            if (category == "tag" && version == Version2)
                return Reply(new { total = 1, items = new[] { new { category, code = "sci-fi", nameZh = "科幻新版" } } }, version);
            return Vocab(request, version);
        });
        using var client = Client(handler);
        var log = new MetadataLogCapture();
        var source = new MetadataDataSource(log, client, new CacheService(DbFactory), new TestTime());

        var tags = await source.GetTagsAsync(42, default);

        Assert.Contains(tags, tag => tag.Name == "科幻新版");
        Assert.DoesNotContain(tags, tag => tag.Name == "科幻");
        Assert.Equal(2, handler.GetPaths.Count(path => path == "/v1/vocab?category=tag"));
        Assert.Equal(LogLevel.Warning, Assert.Single(log.Entries).Level);
    }

    [Fact]
    public async Task ConsistentTagVocabularyDoesNotAnnounceARetry()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/v1/works/42"
            ? Raw(DetailJson)
            : Vocab(request));
        using var client = Client(handler);
        var log = new MetadataLogCapture();
        var source = new MetadataDataSource(log, client, new CacheService(DbFactory), new TestTime());

        var tags = await source.GetTagsAsync(42, default);

        Assert.Contains(tags, tag => tag.Name == "科幻");
        Assert.Equal(4, handler.GetPaths.Count(path => path.StartsWith("/v1/vocab?", StringComparison.Ordinal)));
        Assert.Empty(log.Entries);
    }

    private sealed class MetadataLogCapture : ILogger<MetadataDataSource>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
