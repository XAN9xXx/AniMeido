using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views;
using Microsoft.UI.Xaml.Controls;
using System.Reflection;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopRecommendationReturnTests
{
    [Fact]
    [Trait("Category", "Mechanism")]
    public Task CancellingViewModelDuringCacheWriteKeepsTheBatchForTheNextVisit()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var state = State();
            var source = new Source { Search = (offset, from, _) => Task.FromResult((from is not null ? Enumerable.Range(1, 21).Skip(offset).Take(20).Select(Item).ToList() : [], from is not null ? 21 : 0)) };
            var model = fixture.Recommendation(source, state).ViewModel;
            var service = fixture.Recommendations!;
            await service.SetFeaturePreferenceAsync(new(RecommendationFeatureKind.Tag, "A", "A"), RecommendationAdjustment.Like);
            state.Snapshot = (await service.RefreshAsync())!.Snapshot;
            state.Profile = state.Snapshot.RoundProfile!;
            Assert.Equal(20, state.Snapshot.Items.Count);
            await model.LoadAsync();
            state.SelectedAnimeId = 2; state.Section = "profile"; state.VerticalOffset = 73; state.HasPendingPreferences = true;
            var gate = (SemaphoreSlim)typeof(CacheService).GetField("_mutationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.RecommendationCache)!;
            using var cancellation = new CancellationTokenSource();
            await gate.WaitAsync();
            Task? loading = null;
            try
            {
                loading = model.LoadMoreAsync(cancellation.Token);
                var queued = typeof(SemaphoreSlim).GetField("m_asyncHead", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (queued.GetValue(gate) is null && !loading.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.NotNull(queued.GetValue(gate));
                cancellation.Cancel(); model.Suspend();
            }
            finally { gate.Release(); if (loading is not null) await loading; }
            Assert.Equal(20, state.Snapshot.Items.Count);
            Assert.Equal(21, (await service.GetCachedSnapshotAsync(true))!.Items.Count);
            await model.LoadAsync();
            Assert.Equal(21, model.Items.Count); Assert.False(model.HasMoreRecommendations);
            Assert.Equal(2, model.SelectedItem!.Anime.ID);
            Assert.True(state.HasPendingPreferences); Assert.Equal("profile", state.Section); Assert.Equal(73, state.VerticalOffset);
        });

    [Fact]
    public Task ReturningRestoresACommittedBatchThatTheOldVisitDidNotApply()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var state = State();
            RecommendationFeatureProfile profile = new(new(RecommendationFeatureKind.Tag, "A", "A"), 2, null, []);
            state.Snapshot = state.Snapshot! with { IsPersonalized = true, HasMore = true, RoundProfile = [profile] };
            state.SelectedAnimeId = 2; state.SkippedIds.Add(3); state.HasPendingPreferences = true; state.Section = "profile"; state.VerticalOffset = 73;
            var source = new Source { Search = (offset, from, _) => Task.FromResult((from is not null && offset == 0 ? new List<AniMeido.Contracts.Models.Anime> { Item(4) } : [], from is not null ? 1 : 0)) };
            var model = fixture.Recommendation(source, state).ViewModel;
            await model.LoadAsync();
            model.Suspend();
            var batch = await fixture.Recommendations!.LoadMoreAsync(state.Snapshot, state.SkippedIds);
            Assert.Contains(batch.Items, item => item.Anime.ID == 4);
            Assert.DoesNotContain(state.Snapshot.Items, item => item.Anime.ID == 4);
            await model.LoadAsync();
            Assert.Equal(new[] { 1, 2, 4 }, model.Items.Select(item => item.Anime.ID));
            Assert.Equal(2, model.SelectedItem!.Anime.ID); Assert.False(model.HasMoreRecommendations);
            Assert.True(state.HasPendingPreferences); Assert.Equal("profile", state.Section); Assert.Equal(73, state.VerticalOffset);
        });

    [Fact]
    public Task LoadMoreReconcilesVisibleItemsWithSnapshotAndKeepsBrowsePreferences()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var state = State();
            RecommendationFeatureProfile profile = new(new(RecommendationFeatureKind.Tag, "A", "A"), 2, null, []);
            state.Snapshot = state.Snapshot! with { IsPersonalized = true, HasMore = true, RoundProfile = [profile] };
            state.HasPendingPreferences = true; state.VerticalOffset = 73; state.Section = "profile";
            var source = new Source { Search = (offset, from, _) => Task.FromResult((from is not null && offset == 0 ? new List<AniMeido.Contracts.Models.Anime> { Item(4) } : [], from is not null ? 1 : 0)) };
            var model = fixture.Recommendation(source, state).ViewModel;
            await model.LoadAsync();
            model.SelectedItem = model.Items.Single(item => item.Anime.ID == 1);
            await model.ToggleFollowingAsync(model.SelectedItem, CancellationToken.None);
            await model.LoadMoreAsync();
            Assert.Equal(new[] { 2, 3, 4 }, model.Items.Select(item => item.Anime.ID));
            Assert.Equal(model.Items.Select(item => item.Anime.ID), state.Snapshot!.Items.Select(item => item.Anime.ID));
            Assert.Equal(2, model.SelectedItem!.Anime.ID);
            Assert.Equal(73, state.VerticalOffset); Assert.Equal("profile", state.Section); Assert.True(state.HasPendingPreferences);
        });

    [Fact]
    public Task RatingInDetailsIsExcludedWhenReturningToTheRetainedRecommendationPage()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var state = State(); state.SelectedAnimeId = 2; state.HasPendingPreferences = true; state.SkippedIds.Add(3);
            var feature = new RecommendationFeature(RecommendationFeatureKind.Tag, "旅行", "旅行");
            state.Profile = [new(feature, 2, RecommendationAdjustment.Like, [])];
            var feed = fixture.Recommendation(new Source(), state);
            var details = fixture.Details(new Source());
            try
            {
                await AttachAsync(root, feed); await feed.PendingActivation;
                feed.ShowSection("profile"); state.VerticalOffset = 73;
                await DetachAsync(root, feed);
                await details.OnNavigatedToAsync(2); await AttachAsync(root, details); await details.PendingActivation;
                Invoke((Button)details.FindName("PersonalArchiveButton"));
                var field = typeof(AnimeDetailPage).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (field.GetValue(details) is null && DateTime.UtcNow < deadline) await Task.Delay(10);
                var dialog = Assert.IsType<ContentDialog>(field.GetValue(details)); await Task.Delay(20);
                Assert.IsType<StackPanel>(dialog.Content).Children.OfType<NumberBox>().Single().Value = 8.5;
                Invoke(Find<Button>(dialog, b => b.Name == "PrimaryButton")); await details.PendingOperation;
                await DetachAsync(root, details);
                await AttachAsync(root, feed); await feed.PendingActivation;
                Assert.Equal(1, Assert.Single(feed.ViewModel.Items).Anime.ID);
                Assert.Equal(new[] { 1, 3 }, state.Snapshot!.Items.Select(item => item.Anime.ID));
                Assert.Equal("profile", state.Section); Assert.Equal(73, state.VerticalOffset);
                Assert.True(state.HasPendingPreferences); Assert.Contains(3, state.SkippedIds);
                Assert.Equal(RecommendationAdjustment.Like, Assert.Single(feed.ViewModel.Profile).Adjustment);
            }
            finally
            {
                await DetachAsync(root, feed); await DetachAsync(root, details);
                await feed.PendingActivation; await feed.PendingTags; await feed.PendingPrefetch; await details.PendingOperation;
            }
        });

    [Fact]
    public Task ACancelledMemoryFilterCannotPublishOverTheNextVisitAndKeepsContinuationState()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var state = State(); state.Snapshot = state.Snapshot! with { HasMore = true };
            state.Section = "profile"; state.VerticalOffset = 73; state.HasPendingPreferences = true;
            var source = new Source(); var page = fixture.Recommendation(source, state); var model = page.ViewModel;
            using var oldVisit = new CancellationTokenSource();
            Task oldLoad;
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                oldLoad = model.LoadAsync(oldVisit.Token);
                await Task.Delay(20); oldVisit.Cancel(); model.Suspend();
                state.Snapshot = state.Snapshot with { Items = [state.Snapshot.Items[2]] };
                state.SelectedAnimeId = 3;
            }
            await oldLoad; await model.LoadAsync();
            Assert.Equal(3, Assert.Single(model.Items).Anime.ID); Assert.Equal(3, model.SelectedItem!.Anime.ID);
            Assert.True(model.HasMoreRecommendations); Assert.True(state.HasPendingPreferences);
            Assert.Equal(73, state.VerticalOffset); Assert.Equal("profile", state.Section);
            Assert.Equal(0, source.DetailCalls); Assert.Equal(0, source.TagCalls); Assert.Equal(0, source.ScheduleCalls);
        });
}
