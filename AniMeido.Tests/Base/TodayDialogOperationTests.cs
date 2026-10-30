using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class TodayDialogOperationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingReminderReadRejectsAnotherDialogAndRepeatedReminderManagement(bool repeatReminder)
    {
        var operation = new TodayDialogOperation();
        var read = Signal();
        var showing = Signal();
        var reads = 0;
        var dialogs = 0;
        var managing = ManageRemindersAsync();
        try
        {
            var competing = repeatReminder ? ManageRemindersAsync() : AdjustPlanAsync();
            Assert.True(competing.IsCompletedSuccessfully);
            Assert.Equal(1, reads);
            Assert.Equal(0, dialogs);

            read.TrySetResult();
            await showing.Task.WaitAsync(Budget);
            await managing.WaitAsync(Budget);
            Assert.Equal(1, dialogs);
            await AdjustPlanAsync();
            Assert.Equal(2, dialogs);
        }
        finally
        {
            read.TrySetResult();
            await managing.WaitAsync(Budget);
        }

        async Task ManageRemindersAsync()
        {
            using var admission = operation.TryBegin();
            if (admission is null)
                return;
            reads++;
            await read.Task;
            dialogs++;
            showing.TrySetResult();
        }

        Task AdjustPlanAsync()
        {
            using var admission = operation.TryBegin();
            if (admission is not null)
                dialogs++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ConfirmedSaveRetainsAdmissionUntilItCompletes()
    {
        var operation = new TodayDialogOperation();
        var save = Signal();
        var saving = SaveAsync();
        try
        {
            using var rejected = operation.TryBegin();
            Assert.Null(rejected);
            save.TrySetResult();
            await saving.WaitAsync(Budget);
            using var next = operation.TryBegin();
            Assert.NotNull(next);
        }
        finally
        {
            save.TrySetResult();
            await saving.WaitAsync(Budget);
        }

        async Task SaveAsync()
        {
            using var admission = operation.TryBegin();
            Assert.NotNull(admission);
            await save.Task;
        }
    }

    [Fact]
    public async Task ReadFailureReleasesAdmission()
    {
        var operation = new TodayDialogOperation();
        await Assert.ThrowsAsync<IOException>(OpenAsync);

        using var next = operation.TryBegin();
        Assert.NotNull(next);

        async Task OpenAsync()
        {
            using var admission = operation.TryBegin();
            Assert.NotNull(admission);
            await Task.Yield();
            throw new IOException("synthetic reminder read failure");
        }
    }

    private static TaskCompletionSource Signal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
