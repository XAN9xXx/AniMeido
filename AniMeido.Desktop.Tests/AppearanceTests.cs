using AniMeido.App.Services;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class AppearanceTests
{
    [Fact]
    public void DefaultsKeepRemindersHiddenAndUseCoverCards()
    {
        var appearance = new DesktopAppearance();

        Assert.False(appearance.ShowPlanReminders);
        Assert.Equal(DesktopCatalogCardStyle.Cover, appearance.CatalogCards);
        Assert.Equal(DesktopVisualStyle.Fluent11, appearance.VisualStyle);
        Assert.Equal(DesktopFontStyle.System, appearance.FontStyle);
    }

    [Fact]
    public async Task ChangingVisualStyleKeepsTheChosenFontAndOtherPreferences()
    {
        var initial = new DesktopAppearance(FontStyle: DesktopFontStyle.Serif,
            CatalogCards: DesktopCatalogCardStyle.Detailed, ShowPlanReminders: true);
        DesktopAppearance? persisted = null;
        var editor = new DesktopAppearanceEditor(initial, (value, _) =>
        {
            persisted = value;
            return Task.CompletedTask;
        });

        await editor.UpdateAsync(value => value with { VisualStyle = DesktopVisualStyle.Fluent10 });

        Assert.Equal(initial with { VisualStyle = DesktopVisualStyle.Fluent10 }, editor.Current);
        Assert.Equal(editor.Current, persisted);
    }

    [Fact]
    public async Task AcceptedChangesAreSavedInOrderWithoutLosingAnotherSelection()
    {
        var firstSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<DesktopAppearance>();
        var editor = new DesktopAppearanceEditor(new(), async (value, _) =>
        {
            saved.Add(value);
            if (saved.Count == 1)
            {
                firstSaveStarted.SetResult();
                await releaseFirstSave.Task;
            }
        });

        var first = editor.UpdateAsync(value => value with { VisualStyle = DesktopVisualStyle.Paper });
        await firstSaveStarted.Task;
        var second = editor.UpdateAsync(value => value with { FontStyle = DesktopFontStyle.Serif });
        Assert.False(second.IsCompleted);
        Assert.Single(saved);
        Assert.Equal(DesktopVisualStyle.Paper, editor.Current.VisualStyle);
        Assert.Equal(DesktopFontStyle.Serif, editor.Current.FontStyle);
        releaseFirstSave.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, saved.Count);
        Assert.Equal(DesktopVisualStyle.Paper, saved[1].VisualStyle);
        Assert.Equal(DesktopFontStyle.Serif, saved[1].FontStyle);
        Assert.Equal(saved[1], editor.Current);
    }

    [Fact]
    public async Task FailedSaveKeepsTheChosenAppearanceAndLetsTheNextChangeRun()
    {
        var calls = 0;
        var initial = new DesktopAppearance();
        var editor = new DesktopAppearanceEditor(initial, (_, _) =>
        {
            if (++calls == 1)
                throw new IOException("保存失败");
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<IOException>(() =>
            editor.UpdateAsync(value => value with { VisualStyle = DesktopVisualStyle.Paper }));
        Assert.Equal(initial with { VisualStyle = DesktopVisualStyle.Paper }, editor.Current);
        await editor.UpdateAsync(value => value with { FontStyle = DesktopFontStyle.Serif });

        Assert.Equal(DesktopVisualStyle.Paper, editor.Current.VisualStyle);
        Assert.Equal(DesktopFontStyle.Serif, editor.Current.FontStyle);
    }

    [Fact]
    public async Task AcceptedChoiceIsPublishedBeforeItsSaveCompletes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = new DesktopAppearance();
        var expected = initial with { VisualStyle = DesktopVisualStyle.Paper };
        var changed = new List<DesktopAppearance>();
        var editor = new DesktopAppearanceEditor(initial, (_, _) => release.Task);
        editor.Changed += (_, value) => changed.Add(value);

        var write = editor.UpdateAsync(_ => expected);

        Assert.False(write.IsCompleted);
        Assert.Equal(expected, editor.Current);
        Assert.Equal(expected, Assert.Single(changed));
        release.SetResult();
        await write;
    }

    [Fact]
    public async Task ReturningToTheInitialSelectionStillQueuesTheFinalSnapshot()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<DesktopAppearance>();
        var initial = new DesktopAppearance();
        var editor = new DesktopAppearanceEditor(initial, async (value, _) =>
        {
            saved.Add(value);
            if (saved.Count == 1)
                await release.Task;
        });

        var first = editor.UpdateAsync(value => value with { VisualStyle = DesktopVisualStyle.Paper });
        var second = editor.UpdateAsync(_ => initial);

        Assert.Equal(initial, editor.Current);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, saved.Count);
        Assert.Equal(DesktopVisualStyle.Paper, saved[0].VisualStyle);
        Assert.Equal(initial, saved[1]);
        Assert.Equal(initial, editor.Current);
    }

    [Fact]
    public async Task UnsavedStateTracksTheLastSuccessfulSnapshotRatherThanAnOlderCompletion()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var editor = new DesktopAppearanceEditor(new(), async (_, _) =>
        {
            if (++calls == 1)
                await releaseFirst.Task;
            else
            {
                secondStarted.SetResult();
                await releaseSecond.Task;
            }
        });
        Assert.False(editor.HasUnsavedChanges);

        var first = editor.UpdateAsync(value => value with { VisualStyle = DesktopVisualStyle.Paper });
        var second = editor.UpdateAsync(value => value with { FontStyle = DesktopFontStyle.Serif });
        Assert.True(editor.HasUnsavedChanges);
        releaseFirst.SetResult();
        await secondStarted.Task;
        await first;

        Assert.Equal(DesktopFontStyle.Serif, editor.Current.FontStyle);
        Assert.True(editor.HasUnsavedChanges);
        releaseSecond.SetResult();
        await second;
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task RetrySavesTheCurrentChoiceWithoutPublishingItAgain()
    {
        var calls = 0;
        var changes = 0;
        DesktopAppearance? persisted = null;
        var editor = new DesktopAppearanceEditor(new(), (value, _) =>
        {
            if (++calls == 1)
                throw new IOException("保存失败");
            persisted = value;
            return Task.CompletedTask;
        });
        editor.Changed += (_, _) => ++changes;

        await Assert.ThrowsAsync<IOException>(() => editor.UpdateAsync(
            value => value with { VisualStyle = DesktopVisualStyle.Paper }));
        Assert.True(editor.HasUnsavedChanges);
        await editor.RetrySaveAsync();

        Assert.Equal(2, calls);
        Assert.Equal(1, changes);
        Assert.Equal(editor.Current, persisted);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task RetryKeepsItsAcceptedSnapshotAndRunsBeforeALaterChoice()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<DesktopAppearance>();
        var editor = new DesktopAppearanceEditor(new(), async (value, _) =>
        {
            saved.Add(value);
            if (saved.Count == 1)
                await release.Task;
        });

        var first = editor.UpdateAsync(value => value with { VisualStyle = DesktopVisualStyle.Paper });
        var retried = editor.RetrySaveAsync();
        var last = editor.UpdateAsync(value => value with { FontStyle = DesktopFontStyle.Serif });
        Assert.Equal(DesktopFontStyle.Serif, editor.Current.FontStyle);
        Assert.Single(saved);
        release.SetResult();
        await Task.WhenAll(first, retried, last);

        Assert.Equal(3, saved.Count);
        Assert.Equal(saved[0], saved[1]);
        Assert.Equal(DesktopFontStyle.System, saved[1].FontStyle);
        Assert.Equal(DesktopFontStyle.Serif, saved[2].FontStyle);
        Assert.Equal(saved[2], editor.Current);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task AnOlderFailureDoesNotDiscardTheNewChoiceOrBlockItsSave()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        DesktopAppearance? persisted = null;
        var editor = new DesktopAppearanceEditor(new(), async (value, _) =>
        {
            if (++calls == 1)
            {
                await release.Task;
                throw new IOException("保存失败");
            }
            persisted = value;
        });

        var first = editor.UpdateAsync(value => value with { VisualStyle = DesktopVisualStyle.Paper });
        var second = editor.UpdateAsync(value => value with { ShowPlanReminders = true });
        Assert.True(editor.Current.ShowPlanReminders);
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        await second;

        Assert.Equal(DesktopVisualStyle.Paper, editor.Current.VisualStyle);
        Assert.True(editor.Current.ShowPlanReminders);
        Assert.Equal(editor.Current, persisted);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task AFailedRetryCanBeRetriedAgainWithoutChangingTheSelection()
    {
        var calls = 0;
        var editor = new DesktopAppearanceEditor(new(), (_, _) =>
        {
            if (++calls <= 2)
                throw new IOException("保存失败");
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<IOException>(() => editor.UpdateAsync(
            value => value with { FontStyle = DesktopFontStyle.Serif }));
        await Assert.ThrowsAsync<IOException>(() => editor.RetrySaveAsync());
        Assert.True(editor.HasUnsavedChanges);

        await editor.RetrySaveAsync();

        Assert.Equal(DesktopFontStyle.Serif, editor.Current.FontStyle);
        Assert.False(editor.HasUnsavedChanges);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task PrecancelledRetryDoesNotWriteOrChangeTheCurrentChoice()
    {
        var saves = 0;
        var editor = new DesktopAppearanceEditor(new(), (_, _) =>
        {
            ++saves;
            return Task.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            editor.RetrySaveAsync(cancellation.Token));

        Assert.Equal(0, saves);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task StopDrainsAnAcceptedRetryAndRejectsLaterRetries()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new DesktopAppearanceEditor(new(), (_, _) => release.Task);

        var retried = editor.RetrySaveAsync();
        var stopped = editor.StopAsync();

        Assert.False(stopped.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => { _ = editor.RetrySaveAsync(); });
        release.SetResult();
        await Task.WhenAll(stopped, retried);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task UnchangedSelectionDoesNotWriteAgain()
    {
        var saves = 0;
        var editor = new DesktopAppearanceEditor(new(), (_, _) =>
        {
            saves++;
            return Task.CompletedTask;
        });

        await editor.UpdateAsync(value => value);

        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task StopWaitsForAcceptedSavesAndRejectsLaterChanges()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new DesktopAppearanceEditor(new(), async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
        });
        var write = editor.UpdateAsync(value => value with { ShowPlanReminders = true });
        await started.Task;
        var stopped = editor.StopAsync();

        Assert.False(stopped.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = editor.UpdateAsync(value => value with { FontStyle = DesktopFontStyle.Serif });
        });
        release.SetResult();
        await Task.WhenAll(stopped, write);
        Assert.True(editor.Current.ShowPlanReminders);
    }

    [Fact]
    public async Task CancelledChangeDoesNotSaveAndDoesNotBlockTheNextChange()
    {
        var saves = 0;
        var editor = new DesktopAppearanceEditor(new(), (_, _) =>
        {
            ++saves;
            return Task.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var changes = 0;
        editor.Changed += (_, _) => ++changes;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.UpdateAsync(
            value => value with { ShowPlanReminders = true }, cancellation.Token));
        Assert.Equal(0, changes);
        Assert.False(editor.Current.ShowPlanReminders);
        await editor.UpdateAsync(value => value with { CatalogCards = DesktopCatalogCardStyle.Detailed });

        Assert.Equal(1, saves);
        Assert.False(editor.Current.ShowPlanReminders);
        Assert.Equal(DesktopCatalogCardStyle.Detailed, editor.Current.CatalogCards);
    }
}
