using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ArchiveDirectReadSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private static readonly string[] ExpectedTags = ["alpha", "Beta", "Zulu"];
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;
    private ScreenshotSettings Settings => new(false, Factory.DatabasePath + ".captures", true, false);
    private AnimeArchive? _expectedArchive;

    public enum ReadKind
    {
        ScreenshotSettings,
        ScreenshotTags,
        Archive,
        AnimeTags,
        PersonalRatings,
    }

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        var archive = new ArchiveService(Factory);
        await archive.UpsertArchiveAsync(42, "本地档案标题", 8.5, "保留完整概要\n第二行");
        await archive.UpsertArchiveAsync(43, "尚未评分", null, string.Empty);
        await archive.UpsertArchiveAsync(44, "另一个评分", 6.5, string.Empty);
        await archive.SetAnimeTagsAsync(42, ["Zulu", "alpha", "Beta"]);
        await archive.SetAnimeTagsAsync(43, ["other-anime"]);
        await archive.SaveScreenshotSettingsAsync(Settings);
        foreach (var id in new[] { "selected", "other" })
        {
            await archive.InsertScreenshotAsync(new AnimeScreenshot(
                id, Factory.DatabasePath + "." + id + ".png", "synthetic-record", DateTimeOffset.UtcNow,
                "窗口", "进程", 1280, 720, 42, "本地档案标题", 3, 120, "场景", false));
        }
        await archive.AddScreenshotTagsAsync(["selected"], ["Zulu", "alpha", "Beta"]);
        await archive.AddScreenshotTagsAsync(["other"], ["other-screenshot"]);
        _expectedArchive = await archive.GetArchiveAsync(42);
        Assert.NotNull(_expectedArchive);
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(ReadKind.ScreenshotSettings)]
    [InlineData(ReadKind.ScreenshotTags)]
    [InlineData(ReadKind.Archive)]
    [InlineData(ReadKind.AnimeTags)]
    [InlineData(ReadKind.PersonalRatings)]
    [Trait("Category", "Mechanism")]
    public async Task DirectRead_ReturnsWhileSqliteIsLocked_AndCompletesWithoutTheCallingContext(ReadKind kind)
    {
        var archive = new ArchiveService(Factory);
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync<Task>(() => ReadAsync(archive, kind), context);
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
            case ReadKind.ScreenshotSettings:
                Assert.Equal(Settings, await (Task<ScreenshotSettings>)result);
                break;
            case ReadKind.ScreenshotTags:
                Assert.Equal(ExpectedTags, await (Task<IReadOnlyList<string>>)result);
                Assert.Empty(await archive.GetScreenshotTagsAsync("missing"));
                break;
            case ReadKind.Archive:
                var item = Assert.IsType<AnimeArchive>(await (Task<AnimeArchive?>)result);
                Assert.Equal(_expectedArchive, item);
                Assert.Equal("本地档案标题", item.TitleSnapshot);
                Assert.Equal(8.5, item.PersonalRating);
                Assert.Equal("保留完整概要\n第二行", item.SummaryNote);
                Assert.Null(await archive.GetArchiveAsync(404));
                break;
            case ReadKind.AnimeTags:
                Assert.Equal(ExpectedTags, await (Task<IReadOnlyList<string>>)result);
                Assert.Empty(await archive.GetAnimeTagsAsync(404));
                break;
            case ReadKind.PersonalRatings:
                var ratings = await (Task<IReadOnlyDictionary<int, double>>)result;
                Assert.Equal(2, ratings.Count);
                Assert.Equal(8.5, ratings[42]);
                Assert.Equal(6.5, ratings[44]);
                Assert.False(ratings.ContainsKey(43));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Theory]
    [InlineData(ReadKind.ScreenshotSettings)]
    [InlineData(ReadKind.ScreenshotTags)]
    [InlineData(ReadKind.Archive)]
    [InlineData(ReadKind.AnimeTags)]
    [InlineData(ReadKind.PersonalRatings)]
    public async Task DirectRead_PreservesCancellationAndPropagatesRealSqliteFailures(ReadKind kind)
    {
        var archive = new ArchiveService(Factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(archive, kind, cancellation.Token));

        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            var table = kind switch
            {
                ReadKind.ScreenshotSettings => "config",
                ReadKind.ScreenshotTags => "screenshot_personal_tags",
                ReadKind.AnimeTags => "anime_personal_tags",
                _ => "anime_archives",
            };
            command.CommandText = $"DROP TABLE {table}";
            await command.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<SqliteException>(() => ReadAsync(archive, kind));
        Assert.Equal(1, error.SqliteErrorCode);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScreenshotSettings_MissingOrMalformedJsonKeepsTheDefault(bool malformed)
    {
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = malformed
                ? "UPDATE config SET Value='invalid-json' WHERE Key='screenshot_settings_v1'"
                : "DELETE FROM config WHERE Key='screenshot_settings_v1'";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(ScreenshotSettings.CreateDefault(), await new ArchiveService(Factory).GetScreenshotSettingsAsync());
    }

    private static Task ReadAsync(ArchiveService archive, ReadKind kind, CancellationToken ct = default)
        => kind switch
        {
            ReadKind.ScreenshotSettings => archive.GetScreenshotSettingsAsync(ct),
            ReadKind.ScreenshotTags => archive.GetScreenshotTagsAsync("selected", ct),
            ReadKind.Archive => archive.GetArchiveAsync(42, ct),
            ReadKind.AnimeTags => archive.GetAnimeTagsAsync(42, ct),
            ReadKind.PersonalRatings => archive.GetPersonalRatingsAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
