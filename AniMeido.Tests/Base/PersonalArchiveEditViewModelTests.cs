using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class PersonalArchiveEditViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RepeatedClickDuringReadStartsOnlyOneReadAndDialog()
    {
        var read = Signal<AnimeArchive?>();
        var reads = 0;
        var shown = 0;
        var model = Create((_, _) =>
        {
            reads++;
            return read.Task;
        });
        Task ShowAsync(AnimeArchive? archive, IReadOnlyList<string> tags)
        {
            shown++;
            return Task.CompletedTask;
        }
        var first = model.EditAsync(17, ShowAsync, () => true, default);
        var repeated = model.EditAsync(17, ShowAsync, () => true, default);
        try
        {
            Assert.True(model.IsEditing);
            Assert.Equal(1, reads);
            Assert.True(repeated.IsCompletedSuccessfully);
            Assert.Equal(0, shown);
            read.SetResult(null);
            await first.WaitAsync(Budget);
            Assert.Equal(1, shown);
            Assert.False(model.IsEditing);
        }
        finally
        {
            read.TrySetResult(null);
            await Task.WhenAll(first, repeated).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingDuringEitherReadSkipsTheDialogEvenWhenTheReaderIgnoresCancellation(bool readingTags)
    {
        using var cancellation = new CancellationTokenSource();
        var release = Signal<bool>();
        var entered = Signal<bool>();
        var shown = 0;
        var model = new PersonalArchiveEditViewModel(async (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            if (!readingTags)
            {
                entered.TrySetResult(true);
                await release.Task;
            }
            return null;
        }, async (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            if (readingTags)
            {
                entered.TrySetResult(true);
                await release.Task;
            }
            return [];
        });
        var editing = model.EditAsync(17, (_, _) =>
        {
            shown++;
            return Task.CompletedTask;
        }, () => true, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Budget);
            cancellation.Cancel();
            release.TrySetResult(true);
            await editing.WaitAsync(Budget);
            Assert.Equal(0, shown);
            Assert.False(model.IsEditing);
        }
        finally
        {
            release.TrySetResult(true);
            await editing.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task AReplacementVisitSkipsTheOldDialogAndTheNextVisitCanOpen()
    {
        var read = Signal<AnimeArchive?>();
        var current = true;
        var shown = 0;
        var model = Create((_, _) => read.Task);
        Task ShowAsync(AnimeArchive? archive, IReadOnlyList<string> tags)
        {
            shown++;
            return Task.CompletedTask;
        }
        var old = model.EditAsync(17, ShowAsync, () => current, default);
        try
        {
            current = false;
            read.TrySetResult(null);
            await old.WaitAsync(Budget);
            Assert.Equal(0, shown);
            current = true;
            await model.EditAsync(18, ShowAsync, () => current, default);
            Assert.Equal(1, shown);
        }
        finally
        {
            read.TrySetResult(null);
            await old.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task AnOpenDialogOrConfirmedSaveKeepsTheEditUntilItFinishes()
    {
        var finish = Signal<bool>();
        var shown = 0;
        var model = Create((_, _) => Task.FromResult<AnimeArchive?>(null));
        Task EditAsync(AnimeArchive? archive, IReadOnlyList<string> tags)
        {
            shown++;
            return finish.Task;
        }
        var first = model.EditAsync(17, EditAsync, () => true, default);
        var repeated = model.EditAsync(17, EditAsync, () => true, default);
        try
        {
            Assert.Equal(1, shown);
            Assert.True(repeated.IsCompletedSuccessfully);
            Assert.True(model.IsEditing);
            finish.TrySetResult(true);
            await first.WaitAsync(Budget);
            Assert.False(model.IsEditing);
        }
        finally
        {
            finish.TrySetResult(true);
            await Task.WhenAll(first, repeated).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("tags")]
    [InlineData("dialog")]
    public async Task FailureReleasesTheEditAndAllowsRetry(string failingStage)
    {
        var failing = true;
        var model = new PersonalArchiveEditViewModel(
            (_, _) => failing && failingStage == "archive"
                ? Task.FromException<AnimeArchive?>(new IOException("synthetic read failure"))
                : Task.FromResult<AnimeArchive?>(null),
            (_, _) => failing && failingStage == "tags"
                ? Task.FromException<IReadOnlyList<string>>(new IOException("synthetic tag failure"))
                : Task.FromResult<IReadOnlyList<string>>([]));
        var shown = 0;
        Task ShowAsync(AnimeArchive? archive, IReadOnlyList<string> tags)
        {
            if (failing && failingStage == "dialog")
                throw new IOException("synthetic dialog failure");
            shown++;
            return Task.CompletedTask;
        }
        await Assert.ThrowsAsync<IOException>(() => model.EditAsync(17, ShowAsync, () => true, default));
        Assert.False(model.IsEditing);
        failing = false;
        await model.EditAsync(17, ShowAsync, () => true, default);
        Assert.Equal(1, shown);
        Assert.False(model.IsEditing);
    }

    [Fact]
    public async Task AnAlreadyCancelledClickDoesNotReadOrShow()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reads = 0;
        var shown = 0;
        var model = Create((_, _) =>
        {
            reads++;
            return Task.FromResult<AnimeArchive?>(null);
        });
        await model.EditAsync(17, (_, _) =>
        {
            shown++;
            return Task.CompletedTask;
        }, () => true, cancellation.Token);
        Assert.Equal(0, reads);
        Assert.Equal(0, shown);
        Assert.False(model.IsEditing);
    }

    [Fact]
    public async Task CancellingTheDialogAllowsTheNextClick()
    {
        var shown = 0;
        var identities = new List<int>();
        var model = new PersonalArchiveEditViewModel((id, _) =>
        {
            identities.Add(id);
            return Task.FromResult<AnimeArchive?>(null);
        }, (id, _) =>
        {
            identities.Add(id);
            return Task.FromResult<IReadOnlyList<string>>([]);
        });
        Task CancelAsync(AnimeArchive? archive, IReadOnlyList<string> tags)
        {
            shown++;
            return Task.CompletedTask;
        }
        await model.EditAsync(17, CancelAsync, () => true, default);
        await model.EditAsync(18, CancelAsync, () => true, default);
        Assert.Equal(2, shown);
        Assert.Equal(new[] { 17, 17, 18, 18 }, identities);
        Assert.False(model.IsEditing);
    }

    [Fact]
    public async Task ACancelledReaderReleasesTheEditAndAllowsAnotherVisit()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = Signal<bool>();
        var cancelRead = true;
        var shown = 0;
        var model = Create(async (_, token) =>
        {
            if (cancelRead)
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return null;
        });
        Task ShowAsync(AnimeArchive? archive, IReadOnlyList<string> tags)
        {
            shown++;
            return Task.CompletedTask;
        }
        var editing = model.EditAsync(17, ShowAsync, () => true, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Budget);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editing.WaitAsync(Budget));
            Assert.False(model.IsEditing);
            cancelRead = false;
            await model.EditAsync(18, ShowAsync, () => true, default);
            Assert.Equal(1, shown);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await editing.WaitAsync(Budget);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static PersonalArchiveEditViewModel Create(
        Func<int, CancellationToken, Task<AnimeArchive?>> read)
        => new(read, (_, _) => Task.FromResult<IReadOnlyList<string>>([]));

    private static TaskCompletionSource<T> Signal<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
