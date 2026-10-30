using AniMeido.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using System.Collections.Concurrent;

namespace AniMeido.Tests;

public sealed class TodayThemeArchiveReadSchedulingTests : IAsyncLifetime
{
    private static readonly DateOnly Date = new(2026, 10, 27);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(TodayThemeKind.UnratedThisSeason)]
    [InlineData(TodayThemeKind.FavoriteGenre)]
    [InlineData(TodayThemeKind.SameStudio)]
    [Trait("Category", "Mechanism")]
    public async Task PersonalTheme_ReturnsWhileArchiveIsLocked_AndRecomputesAfterRatingChanges(TodayThemeKind kind)
    {
        var archive = new ArchiveService(Factory);
        var tracking = new TrackingService(Factory);
        var history = new BrowseHistoryService(Factory);
        var actionCenter = new ActionCenterService(Factory);
        foreach (var id in new[] { 1, 2, 3, 42 })
            await tracking.SetStatusAsync(id, AnimeTrackingStatus.Completed);
        await tracking.SetStatusAsync(41, AnimeTrackingStatus.Watching);
        await tracking.SetStatusAsync(203, AnimeTrackingStatus.Blocked);
        await archive.UpsertArchiveAsync(1, "档案出发作品", 9, string.Empty);
        await archive.UpsertArchiveAsync(2, "种子2", 8.5, string.Empty);
        await archive.UpsertArchiveAsync(3, "种子3", 8, string.Empty);
        await actionCenter.RecordAsync(new AnimePlaybackProgress(
            "watched41", 41, 5, 900, 1000, false,
            new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)));
        var rows = await tracking.GetAllTrackingAsync();
        var context = new TodayThemeContext(Date, rows.ToDictionary(row => row.AnimeId, row => row.Status), 204)
        {
            UpdatedAt = rows.ToDictionary(row => row.AnimeId,
                row => DateTimeOffset.Parse(row.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture)),
        };
        var source = new ThemeSource();
        var service = new TodayThemeService(source, tracking, history, archive, actionCenter, NullLogger.Instance);
        using var blocker = await _database.LockExclusiveAsync();
        var ui = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync<Task<TodayThemeContent?>>(() => service.BuildAsync(kind, context, default), ui);
        TodayThemeContent? content;

        try
        {
            var pending = await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(pending.IsCompleted);
            blocker.Dispose();
            // 这里只要求本地读取不占住 UI；主题编排完成后的正常 UI 续体继续执行。
            ui.Resume();
            content = await pending.WaitAsync(CompletionBudget);
        }
        finally
        {
            blocker.Dispose();
            ui.Resume();
            var pending = await invocation.WaitAsync(CompletionBudget);
            await pending.WaitAsync(CompletionBudget);
        }

        Assert.NotNull(content);
        switch (kind)
        {
            case TodayThemeKind.UnratedThisSeason:
                Assert.Equal("2026 年秋季 · 你看完或正在追、还没打分的", content.Caption);
                Assert.Equal(TodayThemeRowKind.Rate, content.RowKind);
                Assert.False(content.ShuffleByDate);
                Assert.Equal(new[] { 42, 41 }, content.Candidates.Select(item => item.Anime.ID));
                Assert.Equal(new[] { "季度作品42", "季度作品41" }, content.Candidates.Select(item => item.Anime.Title));
                Assert.StartsWith("TV · 看完于 ", content.Candidates[0].Meta);
                Assert.Equal("TV · 看到第 5 集", content.Candidates[1].Meta);
                Assert.All(content.Candidates, item => Assert.Empty(item.Note));
                await archive.UpsertArchiveAsync(42, "刚评分的作品", 8, string.Empty);
                var unrated = Assert.IsType<TodayThemeContent>(await service.BuildAsync(kind, context, default));
                Assert.Equal(41, Assert.Single(unrated.Candidates).Anime.ID);
                Assert.Equal(content.Caption, unrated.Caption);
                break;
            case TodayThemeKind.FavoriteGenre:
                Assert.Equal("你打了 8 分以上的 3 部作品里，最常见的题材是“科幻” · 你还没标记的高分作品", content.Caption);
                Assert.Equal(TodayThemeRowKind.Mark, content.RowKind);
                Assert.True(content.ShuffleByDate);
                AssertCandidates(content);
                Assert.Equal(new[] { 1, 2, 3 }, source.TagRequests.Order());
                await archive.UpsertArchiveAsync(3, "种子3", 7, string.Empty);
                var genre = Assert.IsType<TodayThemeContent>(await service.BuildAsync(kind, context, default));
                Assert.Equal("你最近看完的 4 部作品里，最常见的题材是“科幻” · 你还没标记的高分作品", genre.Caption);
                AssertCandidates(genre);
                Assert.Equal(new[] { 1, 1, 2, 2, 3, 3, 42 }, source.TagRequests.Order());
                Assert.Equal(1, source.SearchCount);
                break;
            case TodayThemeKind.SameStudio:
                Assert.Equal("你打了 9 分的《档案出发作品》出自 动画工作室 · 你还没标记的", content.Caption);
                Assert.Equal(TodayThemeRowKind.Mark, content.RowKind);
                Assert.False(content.ShuffleByDate);
                AssertCandidates(content);
                await archive.UpsertArchiveAsync(1, "档案出发作品", null, string.Empty);
                Assert.Null(await service.BuildAsync(kind, context, default));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void AssertCandidates(TodayThemeContent content)
    {
        Assert.Equal(new[] { 202, 201 }, content.Candidates.Select(item => item.Anime.ID));
        Assert.Equal(new[] { "候选202", "候选201" }, content.Candidates.Select(item => item.Anime.Title));
        Assert.Equal(new double?[] { 9, 8 }, content.Candidates.Select(item => item.Anime.Score));
    }

    private sealed class ThemeSource : IAnimeDataSource
    {
        private int _searchCount;
        public int SearchCount => Volatile.Read(ref _searchCount);
        public ConcurrentQueue<int> TagRequests { get; } = new();

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(2026, year);
            Assert.Equal(Season.Fall, season);
            return Task.FromResult(new List<Anime>
            {
                Anime(41, "季度作品41", 8), Anime(1, "已有评分", 9), Anime(42, "季度作品42", 7),
            });
        }

        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            TagRequests.Enqueue(animeID);
            return Task.FromResult(new List<Tag> { new(animeID == 42 ? "日常" : "科幻") });
        }

        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
            CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal("科幻", tag);
            Assert.Equal(0, offset);
            Assert.Equal("rank", sort);
            Interlocked.Increment(ref _searchCount);
            List<Anime> items =
            [
                Anime(201, "候选201", 8), Anime(203, "已屏蔽", 10),
                Anime(202, "候选202", 9), Anime(204, "今日一抽", 9.5),
            ];
            return Task.FromResult((items, items.Count));
        }

        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(animeID == 1
                ? new List<Studio> { new(700, "出版社", null), new(701, "动画工作室", null) }
                : new List<Studio>());
        }

        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(personId switch
            {
                700 => new List<PersonWork> { new(1, "不是动画制作", "原作") },
                701 => new List<PersonWork>
                {
                    new(1, "远端种子名称", "动画制作", Score: 9),
                    new(201, "候选201", "动画制作", Score: 8),
                    new(202, "候选202", "动画制作", Score: 9),
                    new(203, "已屏蔽", "动画制作", Score: 10),
                    new(204, "今日一抽", "动画制作", Score: 9.5),
                    new(205, "不是动画制作", "製作", Score: 10),
                },
                _ => throw new NotSupportedException("Unexpected person lookup."),
            });
        }

        private static Anime Anime(int id, string title, double score)
            => new(id, title, null, [], new DateOnly(2026, 10, 1), null, string.Empty,
                2026, 10, Score: score, MediaFormat: AnimeMediaFormat.Television);

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException("完整候选不应再请求详情。");
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => throw new NotSupportedException("Unexpected broadcast lookup.");
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException("Unexpected voice-actor lookup.");
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException("Unexpected character lookup.");
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => throw new NotSupportedException("Unexpected keyword lookup.");
    }
}
