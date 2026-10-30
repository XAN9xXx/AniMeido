using AniMeido.Contracts.Models;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Plugin.Base.ViewModels;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class TagSearchResultsViewModelTests
{
    [Fact]
    public async Task Initialize_LoadsTheRequestedTargetWithDefaultRankAndFirstTwentyResults()
    {
        var fixture = new QueryFixture(Works(45));
        var vm = fixture.CreateViewModel();

        await vm.InitializeAsync("科幻", 2023, 2026);

        Assert.Equal(("科幻", 2023, 2026), Assert.Single(fixture.Queries));
        Assert.Equal(1, fixture.BlockedReads);
        Assert.Equal(Enumerable.Range(1, 20), vm.Items.Select(item => item.ID));
        Assert.Equal(45, vm.TotalCount);
        Assert.Equal(0, vm.CurrentOffset);
        Assert.Equal("找到 45 部番剧 · 第 1/3 页", vm.ResultText);
        Assert.Equal("1 / 3", vm.PageText);
        Assert.True(vm.HasPages);
        Assert.False(vm.CanGoPrevious);
        Assert.True(vm.CanGoNext);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Paging_KeepsTheCollectionAndDoesNotRepeatTheSearch()
    {
        var fixture = new QueryFixture(Works(45));
        var vm = fixture.CreateViewModel();
        var items = vm.Items;
        await vm.InitializeAsync("科幻", 2023, 2026);

        vm.NextPage();
        Assert.Equal(Enumerable.Range(21, 20), vm.Items.Select(item => item.ID));
        Assert.Equal(20, vm.CurrentOffset);
        Assert.Equal("2 / 3", vm.PageText);
        Assert.True(vm.CanGoPrevious);

        vm.NextPage();
        Assert.Equal(new[] { 41, 42, 43, 44, 45 }, vm.Items.Select(item => item.ID));
        Assert.Equal(40, vm.CurrentOffset);
        Assert.False(vm.CanGoNext);
        vm.NextPage();
        Assert.Equal(40, vm.CurrentOffset);

        vm.PreviousPage();
        Assert.Equal(20, vm.CurrentOffset);
        vm.PreviousPage();
        vm.PreviousPage();
        Assert.Equal(0, vm.CurrentOffset);
        Assert.False(vm.CanGoPrevious);
        Assert.Same(items, vm.Items);
        Assert.Single(fixture.Queries);
    }

    [Fact]
    public async Task Initialize_FiltersBlockedWorksBeforeCountingAndPaging()
    {
        var fixture = new QueryFixture(Works(5)) { Blocked = [2, 4] };
        var vm = fixture.CreateViewModel();

        await vm.InitializeAsync("科幻", 2023, 2026);

        Assert.Equal(new[] { 1, 3, 5 }, vm.Items.Select(item => item.ID));
        Assert.Equal(3, vm.TotalCount);
        Assert.Equal("找到 3 部番剧 · 第 1/1 页", vm.ResultText);
        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public async Task SetSort_PreservesTheExistingRankDateAndMatchSemanticsWithoutSearchingAgain()
    {
        var fixture = new QueryFixture([
            Work(1, 8, new DateOnly(2026, 1, 1)),
            Work(2, 8, new DateOnly(2025, 1, 1)),
            Work(3, null, new DateOnly(2024, 1, 1)),
            Work(4, 9, null),
        ]);
        var vm = fixture.CreateViewModel();
        await vm.InitializeAsync("科幻", 2023, 2026);
        Assert.Equal(new[] { 4, 1, 2, 3 }, vm.Items.Select(item => item.ID));

        vm.SetSort("rank", descending: false);
        Assert.Equal(new[] { 1, 2, 4, 3 }, vm.Items.Select(item => item.ID));
        vm.SetSort("date", descending: false);
        Assert.Equal(new[] { 3, 2, 1, 4 }, vm.Items.Select(item => item.ID));
        vm.SetSort("date", descending: true);
        Assert.Equal(new[] { 4, 1, 2, 3 }, vm.Items.Select(item => item.ID));
        vm.SetSort("match", descending: false);
        Assert.Equal(new[] { 1, 2, 3, 4 }, vm.Items.Select(item => item.ID));
        vm.SetSort("match", descending: true);
        Assert.Equal(new[] { 4, 3, 2, 1 }, vm.Items.Select(item => item.ID));
        Assert.Single(fixture.Queries);
    }

    [Fact]
    public async Task ExplicitSortAndYearChangesReturnToTheFirstPage()
    {
        var fixture = new QueryFixture(Works(45));
        var vm = fixture.CreateViewModel();
        await vm.InitializeAsync("科幻", 2023, 2026);
        vm.NextPage();

        vm.SetSort("match", descending: false);
        Assert.Equal(0, vm.CurrentOffset);
        vm.NextPage();
        await vm.ChangeYearRangeAsync(2022, 2026);

        Assert.Equal(0, vm.CurrentOffset);
        Assert.Equal(Enumerable.Range(1, 20), vm.Items.Select(item => item.ID));
        Assert.Equal(new[] { ("科幻", 2023, 2026), ("科幻", 2022, 2026) }, fixture.Queries);
    }

    [Theory]
    [InlineData(2015, 2026, "年份跨度超过 10 年，请缩小搜索范围")]
    [InlineData(2026, 2025, "起始年份不能晚于结束年份")]
    public async Task InvalidYearRangeDoesNotSearchAndLeavesNoLoadingState(
        int fromYear,
        int toYear,
        string expectedMessage)
    {
        var fixture = new QueryFixture(Works(5));
        var vm = fixture.CreateViewModel();

        await vm.InitializeAsync("科幻", fromYear, toYear);

        Assert.Empty(fixture.Queries);
        Assert.Empty(vm.Items);
        Assert.Equal(0, vm.TotalCount);
        Assert.Equal(expectedMessage, vm.ResultText);
        Assert.False(vm.HasPages);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task YearRangeExactlyTenYearsStillLoadsAllResults()
    {
        var fixture = new QueryFixture(Works(25));
        var vm = fixture.CreateViewModel();

        await vm.InitializeAsync("科幻", 2016, 2026);

        Assert.Equal(("科幻", 2016, 2026), Assert.Single(fixture.Queries));
        Assert.Equal(25, vm.TotalCount);
    }

    [Fact]
    public async Task EmptyResultsKeepTheExistingEmptyMessageAndHidePaging()
    {
        var fixture = new QueryFixture([]);
        var vm = fixture.CreateViewModel();

        await vm.InitializeAsync("科幻", 2023, 2026);

        Assert.Empty(vm.Items);
        Assert.Equal(0, vm.TotalCount);
        Assert.Equal("未找到相关番剧", vm.ResultText);
        Assert.False(vm.HasPages);
        Assert.False(vm.CanGoPrevious);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.IsLoading);
    }

    [Theory]
    [InlineData(false, "搜索失败：合成请求失败")]
    [InlineData(true, "搜索结果解析失败：合成解析失败")]
    public async Task ExpectedSearchFailureClearsLoadingAndKeepsTheExistingMessage(
        bool invalidJson,
        string expectedMessage)
    {
        Exception failure = invalidJson
            ? new JsonException("合成解析失败")
            : new HttpRequestException("合成请求失败");
        var vm = new TagSearchResultsViewModel(
            (_, _, _, _) => Task.FromException<List<Anime>>(failure),
            () => Task.FromResult(new HashSet<int>()), NullLogger.Instance);

        await vm.InitializeAsync("科幻", 2023, 2026);

        Assert.Equal(expectedMessage, vm.ResultText);
        Assert.Empty(vm.Items);
        Assert.False(vm.HasPages);
        Assert.False(vm.IsLoading);
    }

    private static List<Anime> Works(int count)
        => Enumerable.Range(1, count)
            .Select(id => Work(id, 10 - id / 100d, new DateOnly(2026, 1, 1)))
            .ToList();

    private static Anime Work(int id, double? score, DateOnly? date)
        => new(id, $"合成作品{id}", null, [], date, null, string.Empty, 2026, 1, Score: score);

    private sealed class QueryFixture(List<Anime> works)
    {
        public List<(string Tag, int FromYear, int ToYear)> Queries { get; } = [];
        public HashSet<int> Blocked { get; init; } = [];
        public int BlockedReads { get; private set; }

        public TagSearchResultsViewModel CreateViewModel()
            => new((tag, fromYear, toYear, _) =>
            {
                Queries.Add((tag, fromYear, toYear));
                return Task.FromResult(works.ToList());
            }, () =>
            {
                BlockedReads++;
                return Task.FromResult(Blocked.ToHashSet());
            }, NullLogger.Instance);
    }
}
