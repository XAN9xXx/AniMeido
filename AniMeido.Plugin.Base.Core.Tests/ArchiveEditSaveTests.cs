using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ArchiveEditSaveTests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;
    private ArchiveService Archive => new(Factory);

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TagFailureRollsBackTheArchiveVocabularyAndAllAssociations(bool existingArchive)
    {
        await SeedAsync(existingArchive);
        await ExecuteAsync("""
            CREATE TRIGGER reject_fixture_tag BEFORE INSERT ON personal_tags
            WHEN NEW.Name='reject'
            BEGIN SELECT RAISE(ABORT,'fixture tag failure'); END;
            """);
        var before = await ReadStateAsync();

        var failure = await Assert.ThrowsAsync<SqliteException>(() => Archive.SaveArchiveWithTagsAsync(
            42, "修改后的标题", 9.5, "修改后的摘要", ["fresh-before-failure", "reject"]).WaitAsync(Budget));

        Assert.Contains("fixture tag failure", failure.Message);
        AssertState(before, await ReadStateAsync());
        if (!existingArchive)
            Assert.Null(await Archive.GetArchiveAsync(42));
    }

    [Fact]
    public async Task SuccessfulSaveKeepsCreationTimeAndTheExistingNormalizationRules()
    {
        await SeedAsync(existingArchive: true);
        var oldArchive = Assert.IsType<AnimeArchive>(await Archive.GetArchiveAsync(42));
        var otherArchive = await Archive.GetArchiveAsync(99);
        var otherTags = await Archive.GetAnimeTagsAsync(99);
        var tags = new[] { " Alpha ", "alpha", "", new string('x', 51), " Beta " }
            .Concat(Enumerable.Range(1, 31).Select(index => $"tag-{index:00}"))
            .ToArray();
        await Archive.UpsertArchiveAsync(43, "旧标签入口对照", null, "");
        await Archive.SetAnimeTagsAsync(43, tags);
        var expectedTags = await Archive.GetAnimeTagsAsync(43);

        await Archive.SaveArchiveWithTagsAsync(42, " 新标题 ", null, " 新摘要 ", tags).WaitAsync(Budget);

        var updated = Assert.IsType<AnimeArchive>(await Archive.GetArchiveAsync(42));
        Assert.Equal("新标题", updated.TitleSnapshot);
        Assert.Null(updated.PersonalRating);
        Assert.Equal("新摘要", updated.SummaryNote);
        Assert.Equal(oldArchive.CreatedAt, updated.CreatedAt);
        Assert.NotEqual(oldArchive.UpdatedAt, updated.UpdatedAt);
        var actualTags = await Archive.GetAnimeTagsAsync(42);
        Assert.Equal(expectedTags, actualTags);
        Assert.Equal(30, actualTags.Count);
        Assert.Contains("Alpha", actualTags);
        Assert.Contains("Beta", actualTags);
        Assert.Contains("tag-28", actualTags);
        Assert.DoesNotContain("tag-29", actualTags);
        Assert.Equal(otherArchive, await Archive.GetArchiveAsync(99));
        Assert.Equal(otherTags, await Archive.GetAnimeTagsAsync(99));

        await Archive.SaveArchiveWithTagsAsync(44, "新建档案", 8.5, "新建摘要", ["new-archive-tag"])
            .WaitAsync(Budget);

        var created = Assert.IsType<AnimeArchive>(await Archive.GetArchiveAsync(44));
        Assert.Equal("新建档案", created.TitleSnapshot);
        Assert.Equal(8.5, created.PersonalRating);
        Assert.Equal("新建摘要", created.SummaryNote);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.Equal(new[] { "new-archive-tag" }, await Archive.GetAnimeTagsAsync(44));
    }

    [Theory]
    [InlineData(8.25)]
    [InlineData(double.NaN)]
    public async Task InvalidRatingDoesNotChangeTheArchiveOrTags(double rating)
    {
        await SeedAsync(existingArchive: true);
        var before = await ReadStateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Archive.SaveArchiveWithTagsAsync(
            42, "不能保存的标题", rating, "不能保存的摘要", ["cannot-save"]).WaitAsync(Budget));

        AssertState(before, await ReadStateAsync());
    }

    [Fact]
    public async Task PreCancelledSaveDoesNotCreateADatabase()
    {
        var emptyDatabase = new PersonalDatabaseFixture();
        try
        {
            var archive = new ArchiveService(emptyDatabase.Factory);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.False(File.Exists(emptyDatabase.Factory.DatabasePath));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.SaveArchiveWithTagsAsync(
                42, "未保存", 8, "未保存", ["未保存"], cancellation.Token).WaitAsync(Budget));

            Assert.False(File.Exists(emptyDatabase.Factory.DatabasePath));
        }
        finally
        {
            await emptyDatabase.DisposeAsync();
        }
    }

    [Fact]
    public async Task ImportExclusionRejectsTheWholeSaveBeforeAnyChanges()
    {
        await SeedAsync(existingArchive: true);
        var before = await ReadStateAsync();
        using var import = Factory.Access.BeginExclusive();
        await import.WaitForDrainAsync(CancellationToken.None).WaitAsync(Budget);
        Task saving;
        using (ExecutionContext.SuppressFlow())
        {
            saving = Task.Run(() => Archive.SaveArchiveWithTagsAsync(
                42, "导入期间不能保存", 8, "未保存", ["cannot-save"]));
        }
        try
        {
            await Assert.ThrowsAsync<PersonalDataBusyException>(() => saving.WaitAsync(Budget));
            AssertState(before, await ReadStateAsync());
        }
        finally
        {
            await Task.WhenAny(saving).WaitAsync(Budget);
            await saving.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private async Task SeedAsync(bool existingArchive)
    {
        await Archive.UpsertArchiveAsync(99, "其他作品", 7, "其他作品摘要");
        await Archive.SetAnimeTagsAsync(99, ["shared", "other-only"]);
        if (!existingArchive)
            return;

        await Archive.UpsertArchiveAsync(42, "原标题", 7.5, "原摘要");
        await Archive.SetAnimeTagsAsync(42, ["shared", "old-only"]);
        await ExecuteAsync("""
            UPDATE anime_archives
            SET CreatedAt='2021-01-02T00:00:00.0000000+00:00',
                UpdatedAt='2022-03-04T00:00:00.0000000+00:00'
            WHERE AnimeId=42;
            """);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await Factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<DatabaseState> ReadStateAsync()
    {
        var archives = (await Archive.GetArchiveListAsync()).Select(item => item.Archive)
            .OrderBy(item => item.AnimeId).ToArray();
        var vocabulary = new List<(long TagId, string Name)>();
        var associations = new List<(int AnimeId, long TagId)>();
        await using var connection = await Factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TagId, Name FROM personal_tags ORDER BY TagId";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                vocabulary.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        command.CommandText = "SELECT AnimeId, TagId FROM anime_personal_tags ORDER BY AnimeId, TagId";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                associations.Add((reader.GetInt32(0), reader.GetInt64(1)));
        }
        return new DatabaseState(archives, vocabulary.ToArray(), associations.ToArray());
    }

    private static void AssertState(DatabaseState expected, DatabaseState actual)
    {
        Assert.Equal(expected.Archives, actual.Archives);
        Assert.Equal(expected.Vocabulary, actual.Vocabulary);
        Assert.Equal(expected.Associations, actual.Associations);
    }

    private sealed record DatabaseState(
        AnimeArchive[] Archives,
        (long TagId, string Name)[] Vocabulary,
        (int AnimeId, long TagId)[] Associations);
}
