using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed class RatedArchiveThemeSeedQueryTests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task EmptyArchiveReturnsNoThemeSeeds()
    {
        var archive = new ArchiveService(_database.Factory);
        Assert.Empty(await archive.GetRatedArchiveThemeSeedsAsync().WaitAsync(Budget));
    }

    [Fact]
    public async Task ThemeSeedsPreserveTheFourRawFieldsAndExcludeOnlyUnratedArchives()
    {
        await SeedAsync();
        var archive = new ArchiveService(_database.Factory);
        var expected = (await archive.GetArchiveListAsync())
            .Where(item => item.Archive.PersonalRating is not null)
            .Select(item => (item.Archive.AnimeId, item.Archive.TitleSnapshot,
                PersonalRating: item.Archive.PersonalRating!.Value, item.Archive.UpdatedAt))
            .ToArray();

        var actual = await archive.GetRatedArchiveThemeSeedsAsync().WaitAsync(Budget);

        Assert.Equal(expected, actual);
        Assert.Equal(7, actual.Count);
        Assert.DoesNotContain(actual, item => item.AnimeId == 8);
        Assert.Equal(" \t ", Assert.Single(actual, item => item.AnimeId == -1).TitleSnapshot);
        Assert.Equal(string.Empty, Assert.Single(actual, item => item.AnimeId == 2).TitleSnapshot);
        var offset = Assert.Single(actual, item => item.AnimeId == 6);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 19, 0, 0, TimeSpan.Zero), offset.UpdatedAt);
        Assert.Equal(TimeSpan.FromHours(14), offset.UpdatedAt.Offset);
        Assert.Contains(actual, item => item.AnimeId == 7 && item.PersonalRating == 10);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task ThemeSeedReadDoesNotRequireListTagsOrEntryCounts()
    {
        await SeedAsync();
        await using (var connection = await _database.Factory.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE anime_personal_tags; DROP TABLE archive_entries;";
            await command.ExecuteNonQueryAsync();
        }

        var actual = await new ArchiveService(_database.Factory)
            .GetRatedArchiveThemeSeedsAsync().WaitAsync(Budget);

        Assert.Equal(7, actual.Count);
        Assert.Equal(9, Assert.Single(actual, item => item.AnimeId == 5).PersonalRating);
    }

    [Fact]
    public async Task PreCancelledThemeSeedReadDoesNotCreateItsDatabase()
    {
        var untouched = new PersonalDatabaseFixture();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var archive = new ArchiveService(untouched.Factory);
            Assert.False(File.Exists(untouched.Factory.DatabasePath));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive
                .GetRatedArchiveThemeSeedsAsync(cancellation.Token).WaitAsync(Budget));

            Assert.False(File.Exists(untouched.Factory.DatabasePath));
        }
        finally
        {
            await untouched.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameStudioChoosesFiveRatedUnblockedSeedsByAbsoluteUpdateTime()
    {
        await SeedAsync();
        var source = new BoundaryStudioSource();
        var service = new TodayThemeService(source, new TrackingService(_database.Factory),
            new BrowseHistoryService(_database.Factory), new ArchiveService(_database.Factory),
            new ActionCenterService(_database.Factory), NullLogger.Instance);
        var context = new TodayThemeContext(new DateOnly(2026, 10, 30),
            new Dictionary<int, AnimeTrackingStatus> { [7] = AnimeTrackingStatus.Blocked },
            ExcludedAnimeId: null);

        var result = Assert.IsType<TodayThemeContent>(await service
            .BuildAsync(TodayThemeKind.SameStudio, context, default).WaitAsync(Budget));

        Assert.Equal("你打了 9 分的《边界种子》出自 动画工作室 · 你还没标记的", result.Caption);
        Assert.Equal(201, Assert.Single(result.Candidates).Anime.ID);
        Assert.Equal(TodayThemeRowKind.Mark, result.RowKind);
        Assert.False(result.ShuffleByDate);
        Assert.Contains(5, source.Requests);
        Assert.All(source.Requests, id => Assert.Contains(id, new[] { -1, 2, 3, 4, 5 }));
    }

    private async Task SeedAsync()
    {
        await using var connection = await _database.Factory.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO anime_archives(AnimeId, TitleSnapshot, PersonalRating, SummaryNote, CreatedAt, UpdatedAt)
            VALUES(@id, @title, @rating, @summary, @created, @updated);
            """;
        var id = command.Parameters.AddWithValue("@id", 0);
        var title = command.Parameters.AddWithValue("@title", string.Empty);
        var rating = command.Parameters.AddWithValue("@rating", 9);
        var updated = command.Parameters.AddWithValue("@updated", string.Empty);
        command.Parameters.AddWithValue("@summary", new string('文', 8192));
        command.Parameters.AddWithValue("@created", "2020-01-01T00:00:00.0000000+00:00");
        foreach (var row in new (int Id, string Title, double? Rating, string Updated)[]
        {
            (-1, " \t ", 9, "2026-10-30T08:00:00.0000000+00:00"),
            (2, string.Empty, 9, "2026-10-30T07:00:00.0000000+00:00"),
            (3, "种子3", 9, "2026-10-30T06:00:00.0000000+00:00"),
            (4, "种子4", 9, "2026-10-30T05:00:00.0000000+00:00"),
            (5, "边界种子", 9, "2026-10-30T04:00:00.0000000+00:00"),
            (6, "文本较晚但实际较早", 9, "2026-10-30T09:00:00.0000000+14:00"),
            (7, "屏蔽的高分种子", 10, "2026-10-30T10:00:00.0000000+00:00"),
            (8, "未评分", null, "2026-10-30T11:00:00.0000000+00:00"),
        })
        {
            id.Value = row.Id;
            title.Value = row.Title;
            rating.Value = row.Rating is { } value ? value : DBNull.Value;
            updated.Value = row.Updated;
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private sealed class BoundaryStudioSource : IAnimeDataSource
    {
        internal List<int> Requests { get; } = [];

        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(animeID);
            return Task.FromResult(animeID == 5 ? new List<Studio> { new(701, "动画工作室", null) } : []);
        }

        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new List<PersonWork>
            {
                new(5, "远端种子标题", "动画制作", Score: 9),
                new(201, "候选201", "动画制作", Score: 8, AirDate: new DateOnly(2026, 10, 1)),
            });
        }

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
            CancellationToken ct, string? airDateFrom = null, string? airDateTo = null) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
