using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class ArchiveScreenshotSettingsViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly ScreenshotSettings Initial = new(true, "synthetic-directory", false, false);

    [Fact]
    public async Task RepeatedClickWhileReadingStartsOnlyOneReadAndOneDialog()
    {
        var read = NewCompletion<ScreenshotSettings>();
        var reads = 0;
        var shown = 0;
        var model = CreateModel(_ =>
        {
            reads++;
            return read.Task;
        });
        Task<ScreenshotSettings?> first = model.EditAsync(ShowAsync, default);
        Task<ScreenshotSettings?> second = model.EditAsync(ShowAsync, default);

        Task<ScreenshotSettings?> ShowAsync(ScreenshotSettings settings)
        {
            shown++;
            return Task.FromResult<ScreenshotSettings?>(null);
        }

        try
        {
            Assert.True(model.IsEditing);
            Assert.Equal(1, reads);
            Assert.True(second.IsCompletedSuccessfully);
            Assert.Null(await second);
            Assert.Equal(0, shown);
            read.TrySetResult(Initial);
            Assert.Null(await first.WaitAsync(Budget));
            Assert.Equal(1, shown);
            Assert.False(model.IsEditing);
        }
        finally
        {
            read.TrySetResult(Initial);
            await ObserveAsync(first, second);
        }
    }

    [Fact]
    public async Task LeavingDuringReadDoesNotShowADialogEvenIfReadIgnoresCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var read = NewCompletion<ScreenshotSettings>();
        var shown = 0;
        var model = CreateModel(_ => read.Task);
        var editing = model.EditAsync(_ =>
        {
            shown++;
            return Task.FromResult<ScreenshotSettings?>(null);
        }, cancellation.Token);

        try
        {
            cancellation.Cancel();
            read.TrySetResult(Initial);
            Assert.Null(await editing.WaitAsync(Budget));
            Assert.Equal(0, shown);
            Assert.False(model.IsEditing);
        }
        finally
        {
            read.TrySetResult(Initial);
            await ObserveAsync(editing);
        }
    }

    [Fact]
    public async Task RepeatedClickWhileDialogIsOpenDoesNotStartAnotherEdit()
    {
        var dialog = NewCompletion<ScreenshotSettings?>();
        var shown = 0;
        var model = CreateModel(_ => Task.FromResult(Initial));
        Task<ScreenshotSettings?> ShowAsync(ScreenshotSettings settings)
        {
            shown++;
            return dialog.Task;
        }
        var first = model.EditAsync(ShowAsync, default);
        var second = model.EditAsync(ShowAsync, default);

        try
        {
            Assert.Equal(1, shown);
            Assert.True(second.IsCompletedSuccessfully);
            dialog.TrySetResult(null);
            await first.WaitAsync(Budget);
            Assert.False(model.IsEditing);
        }
        finally
        {
            dialog.TrySetResult(null);
            await ObserveAsync(first, second);
        }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("show")]
    [InlineData("save")]
    public async Task FailureReleasesTheEditAndTheNextClickCanRetry(string failingStage)
    {
        var failure = new IOException("截图设置操作失败");
        var fail = true;
        var saves = 0;
        var model = new ArchiveScreenshotSettingsViewModel(
            _ => fail && failingStage == "read"
                ? Task.FromException<ScreenshotSettings>(failure)
                : Task.FromResult(Initial),
            (_, _) =>
            {
                if (fail && failingStage == "save")
                    return Task.FromException(failure);
                saves++;
                return Task.CompletedTask;
            });
        Task<ScreenshotSettings?> ShowAsync(ScreenshotSettings settings)
            => fail && failingStage == "show"
                ? Task.FromException<ScreenshotSettings?>(failure)
                : Task.FromResult<ScreenshotSettings?>(settings);

        var reported = await Assert.ThrowsAsync<IOException>(
            () => model.EditAsync(ShowAsync, default).WaitAsync(Budget));
        Assert.Same(failure, reported);
        Assert.False(model.IsEditing);
        fail = false;
        Assert.Equal(Initial, await model.EditAsync(ShowAsync, default).WaitAsync(Budget));
        Assert.Equal(1, saves);
        Assert.False(model.IsEditing);
    }

    [Fact]
    public async Task CancellingTheDialogDoesNotSave()
    {
        var saves = 0;
        var model = new ArchiveScreenshotSettingsViewModel(
            _ => Task.FromResult(Initial),
            (_, _) =>
            {
                saves++;
                return Task.CompletedTask;
            });

        Assert.Null(await model.EditAsync(_ => Task.FromResult<ScreenshotSettings?>(null), default));
        Assert.Equal(0, saves);
        Assert.False(model.IsEditing);
    }

    [Fact]
    public async Task ConfirmedSaveFinishesWhenThePageLeavesAfterSavingStarts()
    {
        using var cancellation = new CancellationTokenSource();
        var saved = NewCompletion();
        var saveToken = new CancellationToken(true);
        var updated = Initial with { SoundEnabled = true };
        var model = new ArchiveScreenshotSettingsViewModel(
            _ => Task.FromResult(Initial),
            (_, token) =>
            {
                saveToken = token;
                return saved.Task;
            });
        var editing = model.EditAsync(_ => Task.FromResult<ScreenshotSettings?>(updated), cancellation.Token);

        try
        {
            cancellation.Cancel();
            Assert.True(model.IsEditing);
            Assert.False(saveToken.CanBeCanceled);
            saved.TrySetResult();
            Assert.Equal(updated, await editing.WaitAsync(Budget));
            Assert.False(model.IsEditing);
        }
        finally
        {
            saved.TrySetResult();
            await ObserveAsync(editing);
        }
    }

    private static ArchiveScreenshotSettingsViewModel CreateModel(
        Func<CancellationToken, Task<ScreenshotSettings>> read)
        => new(read, (_, _) => Task.CompletedTask);

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveAsync(params Task[] tasks)
    {
        var all = Task.WhenAll(tasks);
        await Task.WhenAny(all).WaitAsync(Budget);
        await all.ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
    }
}
