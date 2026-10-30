using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class TodayThemeTests
{
    [Theory]
    [InlineData(TodayThemeKind.LastSeasonTop, "番剧资料", false)]
    [InlineData(TodayThemeKind.SameStudio, "你的记录 · 番剧资料", true)]
    [InlineData(TodayThemeKind.Stalled, "你的记录", true)]
    public void SourceText_DescribesCurrentDataWithoutChangingSourceClassification(
        TodayThemeKind kind, string expected, bool usesPersonalData)
    {
        Assert.Equal(expected, TodayThemeCatalog.GetSourceText(kind));
        Assert.Equal(usesPersonalData, TodayThemeCatalog.UsesPersonalData(kind));
        Assert.Equal(kind switch
        {
            TodayThemeKind.LastSeasonTop => TodayThemeSource.Bangumi,
            TodayThemeKind.SameStudio => TodayThemeSource.Mixed,
            _ => TodayThemeSource.Personal,
        }, TodayThemeCatalog.GetSource(kind));
    }

    [Theory]
    [InlineData(TodayThemeKind.LastSeasonTop, "2026 年春季 · 你还没标记的 · 综合评分")]
    [InlineData(TodayThemeKind.OneYearAgoSeason, "2025 年秋季 · 你还没标记的 · 综合评分")]
    public async Task SeasonSourceText_UsesCompositeRatingWithoutChangingCandidatesCacheOrStoredTheme(
        TodayThemeKind kind, string expectedCaption)
    {
        await RunProductionMigrationAsync();
        var date = DateWith(kind);
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(120, AnimeTrackingStatus.Completed);
        await _featureState.SaveCalendarDailyPickAsync(date, 119);
        var source = new ThemeSource();
        var service = new TodayThemeService(source, tracking, new BrowseHistoryService(DbFactory),
            new ArchiveService(DbFactory), new ActionCenterService(DbFactory), NullLogger.Instance);
        var context = new TodayThemeContext(date,
            new Dictionary<int, AnimeTrackingStatus> { [120] = AnimeTrackingStatus.Completed },
            ExcludedAnimeId: 119);

        var content = Assert.IsType<TodayThemeContent>(
            await service.BuildAsync(kind, context, CancellationToken.None).WaitAsync(LoggingBudget));

        Assert.Equal(expectedCaption, content.Caption);
        Assert.Equal(Enumerable.Range(101, 18).Reverse(), content.Candidates.Select(candidate => candidate.Anime.ID));
        Assert.Equal(TodayThemeRowKind.Mark, content.RowKind);
        Assert.True(content.ShuffleByDate);
        Assert.Equal(TodayThemeSource.Bangumi, TodayThemeCatalog.GetSource(kind));
        Assert.False(TodayThemeCatalog.UsesPersonalData(kind));
        Assert.Same(content, await service.BuildAsync(kind, context, CancellationToken.None).WaitAsync(LoggingBudget));
        Assert.Equal(1, source.SeasonRequests);

        var vm = NewViewModel(source, tracking);
        await vm.LoadForDateAsync(date).WaitAsync(LoggingBudget);

        Assert.Equal("番剧资料", vm.SourceText);
        Assert.Equal(expectedCaption, vm.Caption);
        Assert.False(vm.IsFallback);
        Assert.False(vm.IsFailed);
        Assert.Equal(TodayThemeViewModel.BatchSize, vm.Items.Count);
        Assert.All(vm.Items, item =>
        {
            Assert.InRange(item.Anime.ID, 101, 118);
            Assert.Equal(TodayThemeRowKind.Mark, item.RowKind);
        });
        var stored = Assert.IsType<TodayThemeState>(await _featureState.LoadTodayThemeAsync());
        Assert.Equal(date, stored.Date);
        Assert.Equal(kind, stored.Theme);
        Assert.False(stored.IsFallback);
        Assert.Equal(vm.Items.Select(item => item.Anime.ID).Order(), stored.AnimeIds.Order());
        Assert.Equal(1, source.SeasonRequests);
    }
}
