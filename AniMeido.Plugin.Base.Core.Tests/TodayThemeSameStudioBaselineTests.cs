using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class TodayThemeSameStudioBaselineTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int SummaryLength = 8192;
    private const int WarmupRounds = 3;
    private const int MeasurementRounds = 5;
    private static readonly DateOnly Date = new(2026, 10, 29);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [Trait("Category", "Performance")]
    public async Task SameStudio_PreservesTheCompleteThemeAndReportsItsCost(int archiveCount)
    {
        await SeedAsync(archiveCount);
        var archive = new ArchiveService(_database.Factory);
        var source = new StudioSource();
        var service = new TodayThemeService(source, new TrackingService(_database.Factory),
            new BrowseHistoryService(_database.Factory), archive, new ActionCenterService(_database.Factory),
            NullLogger.Instance);
        var context = new TodayThemeContext(Date, new Dictionary<int, AnimeTrackingStatus>
        {
            [203] = AnimeTrackingStatus.Blocked,
        }, ExcludedAnimeId: 204);
        var expected = JsonSerializer.Serialize(new TodayThemeContent(
            "你打了 9 分的《作品00001》出自 动画工作室 · 你还没标记的",
            [
                new TodayThemeCandidate(ExpectedAnime(202, 9), "2026"),
                new TodayThemeCandidate(ExpectedAnime(201, 8), "2026"),
            ],
            TodayThemeRowKind.Mark, ShuffleByDate: false));

        for (var warmup = 0; warmup < WarmupRounds; warmup++)
            Assert.Equal(expected, JsonSerializer.Serialize(await service.BuildAsync(TodayThemeKind.SameStudio, context, default)));

        var milliseconds = new double[MeasurementRounds];
        var allocated = new long[MeasurementRounds];
        string? actual = null;
        for (var sample = 0; sample < MeasurementRounds; sample++)
        {
            var bytes = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var result = await service.BuildAsync(TodayThemeKind.SameStudio, context, default);
            milliseconds[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated[sample] = GC.GetTotalAllocatedBytes(precise: true) - bytes;
            actual = JsonSerializer.Serialize(result);
            Assert.Equal(expected, actual);
        }

        output.WriteLine("Archives={0}; Rated={0}; SummaryChars={1}; TagsPerArchive=3; Date={2}; Warmups={3}; Measurements={4}",
            archiveCount, SummaryLength, Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), WarmupRounds, MeasurementRounds);
        output.WriteLine("Top six archives tie at rating 9; UpdatedAt decreases by one day from {0:O}; only the newest seed has matching studio works.", UpdatedAt);
        output.WriteLine("SameStudio: MedianTotalMs={0:F3}; MedianProcessAllocatedBytes={1}; SamplesMs=[{2}]; SamplesBytes=[{3}]",
            milliseconds.Order().ElementAt(MeasurementRounds / 2), allocated.Order().ElementAt(MeasurementRounds / 2),
            string.Join(", ", milliseconds.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))),
            string.Join(", ", allocated));
        output.WriteLine("CompleteThemeJson={0}", actual);
        output.WriteLine("Warm synthetic database; setup, JSON signatures and assertions excluded. Run this class alone: allocations cover the test process. No network or UI measurement.");
    }

    private async Task SeedAsync(int count)
    {
        using var connection = await _database.Factory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO personal_tags(TagId, Name) VALUES(1, '科幻'), (2, '日常'), (3, '已收藏');";
        await command.ExecuteNonQueryAsync();
        command.CommandText = """
            INSERT INTO anime_archives(AnimeId, TitleSnapshot, PersonalRating, SummaryNote, CreatedAt, UpdatedAt)
            VALUES(@id, @title, @rating, @summary, @createdAt, @updatedAt);
            INSERT INTO anime_personal_tags(AnimeId, TagId) VALUES(@id, 1), (@id, 2), (@id, 3);
            """;
        var id = command.Parameters.AddWithValue("@id", 0);
        var title = command.Parameters.AddWithValue("@title", string.Empty);
        var rating = command.Parameters.AddWithValue("@rating", 0);
        var updatedAt = command.Parameters.AddWithValue("@updatedAt", string.Empty);
        command.Parameters.AddWithValue("@createdAt", "2020-01-01T00:00:00.0000000+00:00");
        command.Parameters.AddWithValue("@summary", new string('文', SummaryLength));
        for (var index = 1; index <= count; index++)
        {
            id.Value = index;
            title.Value = $"作品{index:D5}";
            rating.Value = index <= 6 ? 9 : 8;
            updatedAt.Value = UpdatedAt.AddDays(1 - index).ToString("O", CultureInfo.InvariantCulture);
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    private static Anime ExpectedAnime(int id, double score)
        => new(id, $"候选{id}", null, [], new DateOnly(2026, 10, 1), null, string.Empty,
            2026, 10, Score: score);

    private sealed class StudioSource : IAnimeDataSource
    {
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(animeID == 1
                ? new List<Studio> { new(701, "动画工作室", null) }
                : []);
        }

        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new List<PersonWork>
            {
                new(1, "远端种子标题", "动画制作", Score: 9),
                new(201, "候选201", "动画制作", Score: 8, AirDate: new DateOnly(2026, 10, 1)),
                new(202, "候选202", "动画制作", Score: 9, AirDate: new DateOnly(2026, 10, 1)),
                new(203, "已屏蔽", "动画制作", Score: 10),
                new(204, "今日一抽", "动画制作", Score: 9.5),
            });
        }

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException("候选已有评分，不应再读取详情。");
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => throw new NotSupportedException("Unexpected season lookup.");
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => throw new NotSupportedException("Unexpected broadcast lookup.");
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException("Unexpected tag lookup.");
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException("Unexpected voice-actor lookup.");
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException("Unexpected character lookup.");
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
            CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
            => throw new NotSupportedException("Unexpected tag search.");
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => throw new NotSupportedException("Unexpected keyword search.");
    }
}
