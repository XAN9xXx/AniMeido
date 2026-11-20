using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Xunit;
using System.Reflection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;
using AniMeido.Plugin.Base.Views.Controls;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopTodayRecommendationPageTests
{
    [Fact]
    public Task TodayDefersNavigationReadsUntilThePageIsAttached()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new Source();
            var page = fixture.Today(source);
            await page.OnNavigatedToAsync(null);
            try { Assert.Equal(0, source.ScheduleCalls); }
            finally { page.ViewModel.Theme.CancelLoad(); await page.ViewModel.Theme.PendingLoad; }
        });

    [Fact]
    public Task RecommendationDefersNavigationReadsUntilThePageIsAttached()
        => UnoHeadlessTestHost.RunAsync(async _ =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new Source();
            var page = fixture.Recommendation(source, State());
            await page.OnNavigatedToAsync(null);
            Assert.Empty(page.ViewModel.Items);
            Assert.Equal(0, source.TagCalls);
        });

    [Fact]
    public Task TodayRejectsTheInterruptedScheduleWhenTheSamePageReturns()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            await new TrackingService(fixture.Factory).SetStatusAsync(2, AnimeTrackingStatus.Following);
            var delayed = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new Source { Schedule = (call, _) => call == 1 ? delayed.Task : Task.FromResult(new List<Anime> { Item(2) }) };
            var page = fixture.Today(source);
            try
            {
                await AttachAsync(root, page);
                var first = page.PendingActivation;
                Assert.Equal(1, source.ScheduleCalls);
                await DetachAsync(root, page);
                await AttachAsync(root, page);
                await page.PendingActivation;
                delayed.SetResult([Item(1)]);
                await first;
                Assert.Equal(2, source.ScheduleCalls);
                Assert.Equal(2, Assert.Single(page.ViewModel.PersonalBroadcasts).Anime.ID);
                Assert.False(page.ViewModel.IsLoading);
                Assert.Equal(Visibility.Collapsed, ((Border)page.FindName("PlaybackCard")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)page.FindName("PlanFooter")).Visibility);
            }
            finally
            {
                delayed.TrySetResult([]);
                await DetachAsync(root, page);
                await page.PendingActivation;
                await page.ViewModel.Theme.PendingLoad;
            }
        });

    [Fact]
    public Task TodayRefreshKeepsOneInFlightReadAndFiltersTheWholeWeekToPersonalMarks()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            await new TrackingService(fixture.Factory).SetStatusAsync(1, AnimeTrackingStatus.Watching);
            var source = new Source { Schedule = (_, _) => Task.FromResult(new List<Anime> { Item(1), Item(2) }) };
            var page = fixture.Today(source);
            var delayed = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                Assert.Equal(1, Assert.Single(page.ViewModel.PersonalBroadcasts).Anime.ID);
                Assert.All(page.ViewModel.BroadcastDays.SelectMany(day => day.Items), anime => Assert.Equal(1, anime.ID));
                source.Schedule = (_, _) => delayed.Task;
                var refresh = page.RefreshAsync();
                Assert.Same(refresh, page.RefreshAsync());
                Assert.Equal(2, source.ScheduleCalls);
                delayed.SetResult([Item(1)]);
                await refresh;
                Assert.False(page.ViewModel.IsLoading);
            }
            finally
            {
                delayed.TrySetResult([]);
                await DetachAsync(root, page);
                await page.PendingActivation;
                await page.ViewModel.Theme.PendingLoad;
            }
        });

    [Fact]
    public Task PlanOverlayMovesOneSurfaceAndRestoresItOnLeaveAndReturn()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var action = new ActionCenterService(fixture.Factory);
            foreach (var id in Enumerable.Range(1, 5))
                await action.UpsertPlanAsync(id, $"计划{id}", AnimePlanPriority.Normal, null, id);
            var page = fixture.Today(new Source());
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                root.UpdateLayout();
                var host = (Grid)page.FindName("PlanCard");
                var surface = (Grid)page.FindName("PlanVisual");
                await page.OpenPlansAsync();
                Assert.True(page.IsPlanDeckOpen);
                Assert.DoesNotContain(surface, host.Children);
                Assert.Same(surface, Assert.Single(((Canvas)page.FindName("PlanDismissCanvas")).Children));
                await page.OpenPlansAsync();
                Assert.Single(((Canvas)page.FindName("PlanDismissCanvas")).Children);
                await DetachAsync(root, page);
                Assert.False(page.IsPlanDeckOpen);
                Assert.Same(surface, Assert.Single(host.Children));
                await AttachAsync(root, page);
                await page.PendingActivation;
                Assert.Equal(5, page.ViewModel.Plans.Count);
                Assert.Same(surface, Assert.Single(host.Children));
            }
            finally
            {
                await DetachAsync(root, page);
                await page.PendingActivation;
                await page.ViewModel.Theme.PendingLoad;
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ASecondConfirmationIsRejectedAndLeavingClosesTheFirst(bool recommendation)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var today = recommendation ? null : fixture.Today(new Source());
            var feed = recommendation ? fixture.Recommendation(new Source(), State()) : null;
            Page page = today is not null ? today : feed!;
            Task<ContentDialogResult> Show(ContentDialog dialog) => today is not null
                ? today.ShowDialogAsync(dialog, CancellationToken.None) : feed!.ShowDialogAsync(dialog, CancellationToken.None);
            await AttachAsync(root, page);
            await (today?.PendingActivation ?? feed!.PendingActivation);
            try
            {
                var first = Show(new ContentDialog { Title = "确认操作", CloseButtonText = "取消" });
                Assert.False(first.IsCompleted);
                Assert.Equal(ContentDialogResult.None, await Show(new ContentDialog { Title = "重复弹窗" }));
                await DetachAsync(root, page);
                Assert.Equal(ContentDialogResult.None, await first);
                await AttachAsync(root, page);
                await (today?.PendingActivation ?? feed!.PendingActivation);
            }
            finally
            {
                await DetachAsync(root, page);
                if (today is not null) await today.ViewModel.Theme.PendingLoad;
                if (feed is not null) await feed.PendingTags;
            }
        });

    [Fact]
    public Task RecommendationKeepsSelectionSectionAndHorizontalPositionOnReturn()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var state = State(30);
            state.SelectedAnimeId = 12;
            state.VerticalOffset = 420;
            var page = fixture.Recommendation(new Source(), state);
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                await Task.Delay(30);
                Assert.Equal(12, page.ViewModel.SelectedItem?.Anime.ID);
                var scroll = (ScrollViewer)page.FindName("CandidateScroll");
                Assert.InRange(scroll.HorizontalOffset, 419, 421);
                page.ShowSection("profile");
                await DetachAsync(root, page);
                await AttachAsync(root, page);
                await page.PendingActivation;
                Assert.Equal("profile", state.Section);
                Assert.Equal(12, page.ViewModel.SelectedItem?.Anime.ID);
                Assert.Equal(Visibility.Visible, ((Grid)page.FindName("ProfilePanel")).Visibility);
                page.ShowSection("recommendations");
                await Task.Delay(30);
                Assert.InRange(scroll.HorizontalOffset, 419, 421);
            }
            finally { await DetachAsync(root, page); await page.PendingTags; }
        });

    [Fact]
    public Task LateTagsForTheOldSelectionDoNotReplaceTheNewSelection()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var delayed = new TaskCompletionSource<List<Tag>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new Source { Tags = (call, _) => call == 1 ? delayed.Task : Task.FromResult(new List<Tag> { new("新标签") }) };
            var page = fixture.Recommendation(source, State());
            try
            {
                await AttachAsync(root, page);
                while (source.TagCalls == 0) await Task.Delay(10);
                var first = page.PendingActivation;
                page.ViewModel.SelectedItem = page.ViewModel.Items[1];
                await page.PendingTags;
                delayed.SetResult([new("旧标签")]);
                await first;
                Assert.Equal(2, page.ViewModel.SelectedItem?.Anime.ID);
                Assert.Equal("新标签", Assert.Single(page.ViewModel.SelectedTags).Name);
                Assert.False(page.ViewModel.IsLoadingTags);
            }
            finally { delayed.TrySetResult([]); await DetachAsync(root, page); await page.PendingActivation; await page.PendingTags; }
        });

    [Fact]
    public Task HidingAndRestoringUseTheActualPageButtonsAndPersonalDatabase()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.Recommendation(new Source(), State());
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                Invoke((Button)page.FindName("HideSelectedButton"));
                await page.PendingOperation;
                Assert.DoesNotContain(page.ViewModel.Items, item => item.Anime.ID == 1);
                Assert.Equal(1, Assert.Single(page.ViewModel.HiddenAnime).AnimeId);
                Assert.Equal(2, page.ViewModel.SelectedItem?.Anime.ID);
                page.ShowSection("hidden");
                Assert.Equal(Visibility.Visible, ((Grid)page.FindName("HiddenPanel")).Visibility);
            }
            finally { await DetachAsync(root, page); await page.PendingTags; }
        });

    internal static void Invoke(Button button)
        => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    [Fact]
    [Trait("Category", "Mechanism")]
    public Task AnAcceptedHideFinishesAfterLeavingWhileItsOldUiResultStaysDetached()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.Recommendation(new Source(), State());
            var queue = (SemaphoreSlim)typeof(RecommendationService)
                .GetField("_personalWriteGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Recommendations)!;
            await AttachAsync(root, page);
            await page.PendingActivation;
            await queue.WaitAsync();
            try
            {
                Invoke((Button)page.FindName("HideSelectedButton"));
                Assert.False(page.PendingOperation.IsCompleted);
                Invoke((Button)page.FindName("HideSelectedButton"));
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page);
                await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            finally { queue.Release(); }
            try
            {
                await page.PendingOperation;
                await page.PendingActivation;
                Assert.Equal(1, Assert.Single(page.ViewModel.HiddenAnime).AnimeId);
                Assert.DoesNotContain(page.ViewModel.Items, item => item.Anime.ID == 1);
            }
            finally { await DetachAsync(root, page); await page.PendingTags; }
        });

    [Fact]
    public Task CancellingTheReadAfterACommittedHideDoesNotLogASaveFailure()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var log = new PageLog();
            var page = fixture.Recommendation(new Source(), State(), log);
            await AttachAsync(root, page);
            await page.PendingActivation;
            using var visit = new CancellationTokenSource();
            var cancelledAfterCommit = false;
            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(RecommendationViewModel.HasItems)
                    || page.ViewModel.Items.Any(item => item.Anime.ID == 1)) return;
                cancelledAfterCommit = true;
                visit.Cancel();
                page.ViewModel.Suspend();
            }
            page.ViewModel.PropertyChanged += OnChanged;
            try
            {
                var item = page.ViewModel.Items[0];
                await page.ViewModel.RunActionAsync(token => page.ViewModel.HideAsync(item, token), visit.Token);
                Assert.True(cancelledAfterCommit);
                Assert.Equal(1, Assert.Single(await fixture.Recommendations!.GetHiddenAnimeAsync()).AnimeId);
                Assert.Empty(log.Errors);
            }
            finally
            {
                page.ViewModel.PropertyChanged -= OnChanged;
                await DetachAsync(root, page);
                await page.PendingTags;
            }
        });

    [Fact]
    public Task PreCancelledPreferenceAndLeavingTheConfirmationBothKeepPersonalData()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.Recommendation(new Source(), State());
            var feature = new RecommendationFeature(RecommendationFeatureKind.Tag, "科幻", "科幻");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.ViewModel.SetPreferenceAsync(feature,
                RecommendationAdjustment.Like, new CancellationToken(canceled: true)));
            Assert.Empty(await fixture.Recommendations!.GetFeaturePreferencesAsync());
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                Invoke((Button)page.FindName("NotInterestedButton"));
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page);
                await page.PendingOperation;
                Assert.Null(await new TrackingService(fixture.Factory).GetStatusAsync(1));
                Assert.Empty(await fixture.Recommendations.GetHiddenAnimeAsync());
            }
            finally { await DetachAsync(root, page); await page.PendingTags; }
        });

    [Fact]
    [Trait("Category", "Mechanism")]
    public Task ADetachedAcceptedSaveFailureIsLoggedOnceAndTheReturnedPageCanSaveAgain()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var log = new PageLog();
            var page = fixture.Recommendation(new Source(), State(), log);
            var queue = (SemaphoreSlim)typeof(RecommendationService)
                .GetField("_personalWriteGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Recommendations)!;
            await AttachAsync(root, page);
            await page.PendingActivation;
            await queue.WaitAsync();
            try
            {
                using var connection = await fixture.Factory.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER reject_hidden BEFORE INSERT ON recommendation_hidden_anime BEGIN SELECT RAISE(ABORT, 'fixture save rejected'); END";
                await command.ExecuteNonQueryAsync();
                Invoke((Button)page.FindName("HideSelectedButton"));
                await DetachAsync(root, page);
            }
            finally { queue.Release(); }
            try
            {
                await page.PendingOperation;
                Assert.IsType<SqliteException>(Assert.Single(log.Errors));
                Assert.False(((InfoBar)page.FindName("StatusInfoBar")).IsOpen);
                using (var connection = await fixture.Factory.OpenAsync())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DROP TRIGGER reject_hidden";
                    await command.ExecuteNonQueryAsync();
                }
                await AttachAsync(root, page);
                await page.PendingActivation;
                Invoke((Button)page.FindName("HideSelectedButton"));
                await page.PendingOperation;
                Assert.Equal(1, Assert.Single(page.ViewModel.HiddenAnime).AnimeId);
                Assert.Single(log.Errors);
            }
            finally { await DetachAsync(root, page); await page.PendingTags; }
        });

    [Fact]
    public Task CandidateScrollingLoadsTheRemainingPagesWithoutDuplicatesOrChangingSelection()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var profile = new RecommendationFeatureProfile(new(RecommendationFeatureKind.Tag, "科幻", "科幻"), 2.5, null, [], IsSavedTag: true);
            var state = State(1);
            state.Profile = [profile];
            state.Snapshot = state.Snapshot! with { IsPersonalized = true, HasMore = true, RoundProfile = [profile] };
            var offsets = new ConcurrentQueue<int>();
            var source = new Source
            {
                Search = (offset, from, _) =>
                {
                    if (from is null) return Task.FromResult((new List<Anime>(), 0));
                    offsets.Enqueue(offset);
                    return Task.FromResult((Enumerable.Range(offset + 1, Math.Min(20, 45 - offset)).Select(Item).ToList(), 45));
                },
            };
            var page = fixture.Recommendation(source, state);
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                await Task.Delay(30);
                await page.PendingPrefetch;
                var scroll = (ScrollViewer)page.FindName("CandidateScroll");
                scroll.ChangeView(scroll.ScrollableWidth, null, null, true);
                await Task.Delay(30);
                await page.PendingPrefetch;
                for (var attempt = 0; attempt < 6 && page.ViewModel.HasMoreRecommendations; attempt++)
                {
                    scroll.ChangeView(scroll.ScrollableWidth, null, null, true);
                    await Task.Delay(20);
                    await page.PrefetchAsync();
                    await page.PendingPrefetch;
                }
                Assert.True(!page.ViewModel.HasMoreRecommendations,
                    $"count={page.ViewModel.Items.Count}; offsets={string.Join(',', offsets)}; error={page.ViewModel.LoadMoreError}");
                Assert.Equal(45, page.ViewModel.Items.Count);
                Assert.Equal(45, page.ViewModel.Items.Select(item => item.Anime.ID).Distinct().Count());
                Assert.Equal(1, page.ViewModel.SelectedItem?.Anime.ID);
                Assert.Equal(offsets.Count, offsets.Distinct().Count());
                var requests = offsets.Count;
                await page.PrefetchAsync();
                Assert.Equal(requests, offsets.Count);
            }
            finally { await DetachAsync(root, page); await page.PendingTags; await page.PendingPrefetch; }
        });

    [Fact]
    [Trait("Category", "Mechanism")]
    public Task AcceptedOnboardingCompletesTheWholeTagSelectionAfterLeaving()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var page = fixture.Recommendation(new Source(), State());
            var queue = (SemaphoreSlim)typeof(RecommendationService)
                .GetField("_personalWriteGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Recommendations)!;
            await AttachAsync(root, page);
            await page.PendingActivation;
            page.ViewModel.SuggestedTags[0].IsSelected = true;
            page.ViewModel.SuggestedTags[1].IsSelected = true;
            var selected = page.ViewModel.SelectedOnboardingTags;
            await queue.WaitAsync();
            try
            {
                var generate = (Button)page.FindName("GenerateFromTagsButton");
                generate.IsEnabled = true;
                Invoke(generate);
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page);
            }
            finally { queue.Release(); }
            try
            {
                await page.PendingOperation;
                var saved = await fixture.Recommendations!.GetFeaturePreferencesAsync();
                Assert.Equal(selected.Order(), saved.Select(item => item.DisplayName).Order());
                Assert.All(saved, item => Assert.Equal(RecommendationAdjustment.Like, item.Adjustment));
            }
            finally { await DetachAsync(root, page); await page.PendingTags; }
        });

    [Fact]
    [Trait("Category", "Mechanism")]
    public Task APlanSaveSurvivesARepeatedEditAndReentryWaitsForItsCommit()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var action = new ActionCenterService(fixture.Factory);
            await action.UpsertPlanAsync(1, "计划1", AnimePlanPriority.Normal, null, 0);
            var page = fixture.Today(new Source());
            await AttachAsync(root, page);
            await page.PendingActivation;
            await page.ViewModel.Theme.PendingLoad;
            var edit = Find<Button>(page, button => Equals(button.Content, "调整计划"));
            Invoke(edit);
            await Task.Delay(20);
            var dialog = Assert.IsType<ContentDialog>(typeof(TodayPage).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
            Assert.IsType<StackPanel>(dialog.Content).Children.OfType<NumberBox>().Single().Value = 31;
            using (var blocker = await LockDatabaseAsync(fixture))
            {
                Invoke(Find<Button>(dialog, button => button.Name == "PrimaryButton"));
                await Task.Delay(20);
                Assert.False(page.PendingOperation.IsCompleted);
                Invoke(edit);
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page);
                await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            try
            {
                await page.PendingOperation;
                await page.PendingActivation;
                Assert.Equal(31, (await action.GetPlanAsync(1))!.SortOrder);
                Assert.Equal(31, Assert.Single(page.ViewModel.Plans).Plan.SortOrder);
            }
            finally { await DetachAsync(root, page); await page.ViewModel.Theme.PendingLoad; }
        });

    [Fact]
    [Trait("Category", "Mechanism")]
    public Task TagChoiceSaveIsRegisteredBeforeLeavingAndReturning()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new Source { Tags = (_, _) => Task.FromResult(new List<Tag> { new("科幻"), new("冒险") }) };
            var page = fixture.Recommendation(source, State());
            await AttachAsync(root, page);
            await page.PendingActivation;
            ((Expander)page.FindName("TagPreferencesExpander")).IsExpanded = true;
            root.UpdateLayout();
            await Task.Delay(20);
            var choice = Find<DesktopChoice>(page, item => item.Tag is RecommendationTagPreference { Name: "科幻" });
            var picker = Find<ComboBox>(choice, item => item.Visibility == Visibility.Visible);
            var queue = (SemaphoreSlim)typeof(RecommendationService)
                .GetField("_personalWriteGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Recommendations)!;
            await queue.WaitAsync();
            try
            {
                picker.SelectedIndex = 0;
                Assert.False(page.PendingOperation.IsCompleted);
                picker.SelectedIndex = 2;
                Assert.False(page.PendingOperation.IsCompleted);
                await DetachAsync(root, page);
                await AttachAsync(root, page);
                Assert.False(page.PendingActivation.IsCompleted);
            }
            finally { queue.Release(); }
            try
            {
                await page.PendingOperation;
                await page.PendingActivation;
                Assert.Equal(RecommendationAdjustment.Like, Assert.Single(await fixture.Recommendations!.GetFeaturePreferencesAsync()).Adjustment);
                Assert.True(page.ViewModel.SelectedTags.Single(tag => tag.Name == "科幻").IsLiked);
            }
            finally { await DetachAsync(root, page); await page.PendingTags; }
        });

    [Fact]
    public Task CollapsedTagDeckKeepsRearFramesAndExpandedRowsRestoreKeyboardStops()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var panel = new TodayPlanStackPanel { Width = 240, VisibleCardCount = 1, FanAngleStep = 1.5, HideRearContent = true };
            var faces = new List<StackPanel>();
            var buttons = new List<Button>();
            for (var index = 0; index < 5; index++)
            {
                var button = new Button { Content = $"标签{index}", IsTabStop = true };
                var face = new StackPanel { Name = "CardPackFace" };
                face.Children.Add(button);
                faces.Add(face); buttons.Add(button);
                panel.Children.Add(new Border { Height = 60, Child = face });
            }
            root.Children.Add(panel);
            await Task.Delay(20);
            root.UpdateLayout();
            Assert.Equal(1, panel.Children[1].Opacity);
            Assert.Equal(0, faces[1].Opacity);
            Assert.Equal(1.5, Assert.IsType<RotateTransform>(panel.Children[1].RenderTransform).Angle);
            Assert.False(buttons[1].IsTabStop);
            Assert.Equal(0, panel.Children[4].Opacity);
            panel.Expanded = true;
            root.UpdateLayout();
            Assert.All(faces, face => Assert.Equal(1, face.Opacity));
            Assert.All(buttons, button => Assert.True(button.IsTabStop));
            Assert.All(panel.Children, child => Assert.Equal(0, Assert.IsType<RotateTransform>(child.RenderTransform).Angle));
        });

    internal static T Find<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        return Search(root) ?? throw new InvalidOperationException($"页面缺少 {typeof(T).Name}。");
        T? Search(DependencyObject node)
        {
            if (node is T element && predicate(element)) return element;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                if (Search(VisualTreeHelper.GetChild(node, index)) is { } child) return child;
            return null;
        }
    }

    internal static async Task<SqliteConnection> LockDatabaseAsync(Fixture fixture)
    {
        using (var pooled = fixture.Factory.CreateConnection()) SqliteConnection.ClearPool(pooled);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath, Cache = SqliteCacheMode.Private, Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = "PRAGMA journal_mode=DELETE";
            await command.ExecuteScalarAsync();
            command.CommandText = "BEGIN EXCLUSIVE";
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static RecommendationBrowseState State(int count = 3) => new()
    {
        Snapshot = new(RecommendationSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow, false,
            Enumerable.Range(1, count).Select(id => new RecommendationItem(Item(id), 1, [], false, true)).ToArray()),
    };

    internal static Anime Item(int id) => new(id, $"作品{id}", null, [], DateOnly.FromDateTime(DateTime.Today),
        null, "合成作品简介", DateTime.Today.Year, DateTime.Today.Month,
        DateTime.Today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)DateTime.Today.DayOfWeek);

    internal static async Task AttachAsync(Grid root, Page page)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Loaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
        page.Loaded += Loaded;
        try { root.Children.Add(page); await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { page.Loaded -= Loaded; }
    }

    internal static async Task DetachAsync(Grid root, Page page)
    {
        if (!root.Children.Contains(page)) return;
        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Unloaded(object sender, RoutedEventArgs args) => unloaded.TrySetResult();
        page.Unloaded += Unloaded;
        try { root.Children.Remove(page); await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { page.Unloaded -= Unloaded; }
    }

    internal sealed class Fixture : IAsyncDisposable, IAppDataPaths
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("AniMeido-page-").FullName;
        private readonly List<IAsyncDisposable> _services = [];
        private readonly List<IDisposable> _registrations = [];
        internal SqliteConnectionFactory Factory { get; }
        internal Navigator Navigation { get; } = new();
        internal RecommendationService? Recommendations { get; private set; }
        internal CacheService? RecommendationCache { get; private set; }
        internal ScreenshotShortcutAction? ScreenshotShortcut { get; private set; }
        private Fixture() => Factory = new(this);
        public string DatabasePath => Path.Combine(_directory, "personal.db");
        public string BackupDirectory => Path.Combine(_directory, "backups");
        public string LogDirectory => Path.Combine(_directory, "logs");

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            using var connection = await fixture.Factory.OpenAsync();
            await PersonalDatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE config(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync();
            await PersonalDatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
            await PersonalDatabaseSchema.RunMigrationsAsync(connection);
            await PersonalDatabaseSchema.UpgradeEmptyDatabaseAsync(connection);
            return fixture;
        }

        internal TodayPage Today(Source source)
        {
            var action = new ActionCenterService(Factory);
            var reminders = new PlanReminderCoordinator(action, new Notifications(), Navigation);
            _registrations.Add(reminders);
            return new(source, new TrackingService(Factory), new PersonalFeatureStateStore(Factory), action,
                reminders, new BrowseHistoryService(Factory), new ArchiveService(Factory), Navigation,
                new Playback(), NullLogger<TodayPage>.Instance);
        }

        internal RecommendationPage Recommendation(Source source, RecommendationBrowseState state, ILogger<RecommendationPage>? logger = null)
        {
            var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
            RecommendationCache = new CacheService(Factory);
            var service = new RecommendationService(Factory, new TrackingService(Factory), new SavedTagService(Factory),
                new ArchiveService(Factory), new BrowseHistoryService(Factory), new ActionCenterService(Factory),
                RecommendationCache, candidates);
            _services.Add(service);
            Recommendations = service;
            _services.Add(candidates);
            return new(service, Navigation, state, logger ?? NullLogger<RecommendationPage>.Instance);
        }

        internal ManagementPage Mine(Source source)
        {
            var tracking = new TrackingService(Factory);
            var search = (LocalSearchService)Activator.CreateInstance(typeof(LocalSearchService),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [tracking, source, NullLogger<LocalSearchService>.Instance], null)!;
            return new(tracking, source, new SavedTagService(Factory), search, new ArchiveService(Factory), Navigation,
                NullLogger<ManagementPage>.Instance);
        }

        internal AnimeDetailPage Details(Source source)
        {
            var client = new HttpClient(new RatingHandler()) { BaseAddress = new Uri("https://api.animeido.com:38443") };
            _registrations.Add(client);
            var ratings = (MetadataDataSource)Activator.CreateInstance(typeof(MetadataDataSource),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [NullLogger<MetadataDataSource>.Instance, client, new CacheService(Factory), null], null)!;
            return new(source, new TrackingService(Factory), new SavedTagService(Factory), new BrowseHistoryService(Factory),
                Navigation, new ArchiveService(Factory), [], ratings, NullLogger<AnimeDetailPage>.Instance);
        }

        internal ArchivePage Archives(Source source)
        {
            var archive = new ArchiveService(Factory);
            var remap = new PersonalIdRemapService(Factory, NullLogger<PersonalIdRemapService>.Instance);
            var screenshots = new ScreenshotArchiveService(archive, null!, null!, null!);
            ScreenshotShortcut = new(screenshots, archive, new WindowActivation(), Navigation);
            _registrations.Add(ScreenshotShortcut);
            return new(archive, new ArchiveQueryService(archive), new ArchiveReviewExporter(archive), source,
                new Playback(), screenshots, ScreenshotShortcut, new NoWindow(), NullLogger<ArchivePage>.Instance,
                new PendingReviewService(Factory, remap), remap);
        }

        internal DataManagementSettingsPage DataSettings()
        {
            var archive = new ArchiveService(Factory);
            var export = new ExportService(Factory);
            var backup = new BackupService(Factory, this);
            var bundle = new ArchiveBundleService(export, archive, backup, NullLogger<ArchiveBundleService>.Instance);
            return new(new CacheService(Factory), new PersonalDataTransferService(Factory, export, backup, bundle), this,
                new NoWindow(), NullLogger<DataManagementSettingsPage>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var service in _services) await service.DisposeAsync();
            foreach (var registration in _registrations) registration.Dispose();
            await Factory.StopAsync();
            using var connection = Factory.CreateConnection();
            SqliteConnection.ClearPool(connection);
            Directory.Delete(_directory, recursive: true);
        }
    }

    internal sealed class Navigator : IPluginNavigator, IPlanReminderNavigation
    {
        internal object? Parameter { get; private set; }
        internal Type? Page { get; private set; }
        public void Navigate(Type pageType, object? parameter = null) { Page = pageType; Parameter = parameter; }
        public void OpenPlan(int animeId) { }
    }

    internal sealed class Source : IAnimeDataSource
    {
        internal int ScheduleCalls { get; private set; }
        internal int TagCalls { get; private set; }
        internal int DetailCalls { get; private set; }
        internal Func<int, CancellationToken, Task<Anime?>> Detail { get; set; } = (id, _) => Task.FromResult<Anime?>(Item(id));
        internal Func<int, CancellationToken, Task<List<CharacterRole>>> Characters { get; set; } = (_, _) => Task.FromResult(new List<CharacterRole>());
        internal Func<int, CancellationToken, Task<List<Anime>>> Schedule { get; set; } = (_, _) => Task.FromResult(new List<Anime>());
        internal Func<CancellationToken, Task<List<Anime>>> Seasonal { get; set; } = _ => Task.FromResult(new List<Anime>());
        internal Func<int, CancellationToken, Task<List<Tag>>> Tags { get; set; } = (_, _) => Task.FromResult(new List<Tag>());
        internal Func<int, string?, CancellationToken, Task<(List<Anime>, int)>> Search { get; init; } = (_, _, _) => Task.FromResult((new List<Anime>(), 0));
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => Schedule(++ScheduleCalls, ct);
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => Seasonal(ct);
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) { DetailCalls++; return Detail(animeID, ct); }
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Tags(++TagCalls, ct);
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<Studio>());
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<VoiceActor>());
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Characters(animeID, ct);
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Task.FromResult(new List<PersonWork>());
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null) => Search(offset, airDateFrom, ct);
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset,
            CancellationToken ct) => Task.FromResult((new List<Anime>(), 0, offset));
    }

    private sealed class Playback : IAnimePlaybackLauncher
    {
        public bool IsAvailable => false;
        public event EventHandler? AvailabilityChanged { add { } remove { } }
        public Task LaunchAsync(AnimePlaybackContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoWindow : AniMeido.Contracts.Desktop.IWindowHandleProvider
    {
        public bool TryGetWindowHandle(out nint handle) { handle = 0; return false; }
    }

    private sealed class WindowActivation : AniMeido.Contracts.Desktop.IAppWindowActivationService
    {
        public void ActivateMainWindow() { }
    }

    private sealed class RatingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var result = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"dataVersion":"fixture-v1","data":{"id":1,"firstDate":"2020-01-01","ratings":[{"platform":"bangumi","score":8.1,"votes":12000},{"platform":"mal","score":8.4,"votes":18000}]}}""", System.Text.Encoding.UTF8, "application/json"),
            };
            result.Headers.Add("X-Data-Version", "fixture-v1");
            return Task.FromResult(result);
        }
    }

    private sealed class PageLog : ILogger<RecommendationPage>
    {
        internal List<Exception?> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errors.Add(exception);
        }
    }

    private sealed class Notifications : IAppNotificationService, IDisposable
    {
        public bool IsSupported => true;
        public bool NotificationsEnabled => true;
        public Task ScheduleAsync(AppNotificationRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelAsync(string group, string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelGroupAsync(string group, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IDisposable RegisterActivationHandler(string category, Func<AppNotificationActivation, CancellationToken, Task> handler) => this;
        public Task OpenNotificationSettingsAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
