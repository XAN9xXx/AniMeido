using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed partial class ManagementViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveTag_CommittedPreviousLoadCannotChangeTheCurrentView(bool reload)
    {
        var tagReads = 0;
        var source = new ManagementSource
        {
            TagSearch = (tag, _) =>
            {
                Interlocked.Increment(ref tagReads);
                return Task.FromResult((new List<Anime>
                {
                    Item(tag == "B-new" ? 2 : 10),
                }, 1));
            },
        };
        var vm = await CreateViewModelAsync(source);
        await vm.ShowTagsAsync().WaitAsync(Budget);
        var context = new PausedUiContext();
        using var blocker = await LockTagWriteDatabaseAsync();
        var removing = BeginPausedTagWriteAsync(() => vm.RemoveTagAsync("A-old"), context);
        try
        {
            Assert.False(removing.IsCompleted);
            vm.CancelPendingLoads();
            blocker.Dispose();
            await context.FirstPost.WaitAsync(Budget);
            Assert.False(removing.IsCompleted);
            Assert.Equal(new[] { "B-new" }, await new SavedTagService(DbFactory).GetAllSavedTagsAsync());

            if (reload)
                await vm.LoadAsync().WaitAsync(Budget);

            var currentTags = vm.TagList.ToArray();
            var currentEntries = vm.TagEntries.ToArray();
            var selectedTag = vm.SelectedTag;
            var tagSummary = vm.TagResultSummary;
            var currentReadCount = Volatile.Read(ref tagReads);
            vm.IsError = true;
            vm.ErrorMessage = "保留当前访问的提示";
            context.Resume();
            await removing.WaitAsync(Budget);

            Assert.Equal(currentTags, vm.TagList);
            Assert.Equal(currentEntries, vm.TagEntries);
            Assert.Equal(selectedTag, vm.SelectedTag);
            Assert.Equal(currentTags.Length, vm.TagCount);
            Assert.Equal(tagSummary, vm.TagResultSummary);
            Assert.True(vm.IsError);
            Assert.Equal("保留当前访问的提示", vm.ErrorMessage);
            Assert.False(vm.IsLoading);
            Assert.False(vm.IsTagLoading);
            Assert.Equal(currentReadCount, Volatile.Read(ref tagReads));
            Assert.Equal(new[] { "B-new" }, await new SavedTagService(DbFactory).GetAllSavedTagsAsync());

            await vm.LoadAsync().WaitAsync(Budget);
            Assert.Equal("B-new", Assert.Single(vm.TagList).TagName);
            Assert.Equal("B-new", vm.SelectedTag);
            Assert.Equal(2, Assert.Single(vm.TagEntries).Anime.ID);
            Assert.False(vm.IsError);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            try
            {
                await ObserveTagWriteAsync(removing).WaitAsync(Budget);
            }
            finally
            {
                vm.CancelPendingLoads();
            }
        }
    }

    private static async Task ObserveTagWriteAsync(Task task)
        => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private static Task BeginPausedTagWriteAsync(Func<Task> action, PausedUiContext context)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private async Task<SqliteConnection> LockTagWriteDatabaseAsync()
    {
        using (var pooled = DbFactory.CreateConnection())
            SqliteConnection.ClearPool(pooled);

        var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await blocker.OpenAsync();
            using var command = blocker.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = "PRAGMA journal_mode=DELETE";
            Assert.Equal("delete", await command.ExecuteScalarAsync());
            command.CommandText = "BEGIN EXCLUSIVE";
            await command.ExecuteNonQueryAsync();
            return blocker;
        }
        catch
        {
            blocker.Dispose();
            throw;
        }
    }
}
