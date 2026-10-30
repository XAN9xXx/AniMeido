using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class SavedTagSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    public enum SavedTagOperationKind
    {
        GetAll,
        GetStates,
        IsSaved,
        Save,
        Remove,
    }

    [Theory]
    [InlineData(SavedTagOperationKind.GetAll)]
    [InlineData(SavedTagOperationKind.GetStates)]
    [InlineData(SavedTagOperationKind.IsSaved)]
    [InlineData(SavedTagOperationKind.Save)]
    [InlineData(SavedTagOperationKind.Remove)]
    public async Task SavedTagOperation_ReturnsWhileSqliteIsLocked_AndFinishesWithoutTheCallingContext(
        SavedTagOperationKind kind)
    {
        await SeedTagsAsync();
        var tags = new SavedTagService(Factory);
        using var blocker = await _database.LockExclusiveAsync();
        Func<Task> call = kind switch
        {
            SavedTagOperationKind.GetAll => () => tags.GetAllSavedTagsAsync(),
            SavedTagOperationKind.GetStates => () => tags.GetSavedTagStatesAsync(),
            SavedTagOperationKind.IsSaved => () => tags.IsTagSavedAsync("z'quoted"),
            SavedTagOperationKind.Save => () => tags.SaveTagAsync("new-tag"),
            SavedTagOperationKind.Remove => () => tags.RemoveTagAsync("beta"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var context = new PausedUiContext();
        var returned = SchedulingTestThread.InvokeAsync<Task>(call, context);
        IDisposable? import = null;
        Task? drained = null;
        Task operation;
        try
        {
            operation = await returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);
            if (kind is SavedTagOperationKind.Save or SavedTagOperationKind.Remove)
            {
                var exclusive = Factory.Access.BeginExclusive();
                import = exclusive;
                drained = exclusive.WaitForDrainAsync(CancellationToken.None);
                Assert.False(drained.IsCompleted);
            }

            blocker.Dispose();
            await operation.WaitAsync(CompletionBudget);
            Assert.Equal(0, context.PostCount);
            if (drained is not null)
            {
                await drained.WaitAsync(CompletionBudget);
            }
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            try
            {
                var pending = await returned.WaitAsync(CompletionBudget);
                await Task.WhenAny(pending).WaitAsync(CompletionBudget);
                await pending.ConfigureAwait(
                    ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
            finally
            {
                import?.Dispose();
            }
        }

        switch (kind)
        {
            case SavedTagOperationKind.GetAll:
                Assert.Equal(new[] { "Alpha", "beta", "invalid", "z'quoted" },
                    await (Task<List<string>>)operation);
                break;
            case SavedTagOperationKind.GetStates:
                Assert.Equal(new[]
                {
                    new SavedTagState("Alpha", false),
                    new SavedTagState("beta", false),
                    new SavedTagState("invalid", true),
                    new SavedTagState("z'quoted", false),
                }, await (Task<List<SavedTagState>>)operation);
                break;
            case SavedTagOperationKind.IsSaved:
                Assert.True(await (Task<bool>)operation);
                break;
            case SavedTagOperationKind.Save:
                Assert.Equal(new[] { "Alpha", "beta", "invalid", "new-tag", "z'quoted" },
                    await tags.GetAllSavedTagsAsync());
                break;
            case SavedTagOperationKind.Remove:
                Assert.Equal(new[] { "Alpha", "invalid", "z'quoted" }, await tags.GetAllSavedTagsAsync());
                break;
        }
    }

    [Fact]
    public async Task SavedTagOperations_KeepCountComparisonDuplicateInvalidStateAndSequentialWrites()
    {
        await SeedTagsAsync();
        var tags = new SavedTagService(Factory);

        Assert.True(await tags.IsTagSavedAsync("z'quoted"));
        Assert.True(await tags.IsTagSavedAsync("invalid"));
        Assert.False(await tags.IsTagSavedAsync("missing"));
        Assert.False(await tags.IsTagSavedAsync("alpha"));

        await tags.SaveTagAsync("invalid");
        var invalid = Assert.Single(await tags.GetSavedTagStatesAsync(), tag => tag.Name == "invalid");
        Assert.True(invalid.Invalid);
        Assert.Equal("invalid", invalid.Name);

        await tags.SaveTagAsync("new-tag");
        await tags.SaveTagAsync("new-tag");
        await tags.RemoveTagAsync("beta");
        await tags.RemoveTagAsync("missing");

        Assert.Equal(new[] { "Alpha", "invalid", "new-tag", "z'quoted" }, await tags.GetAllSavedTagsAsync());
        Assert.False(await tags.IsTagSavedAsync("beta"));
        Assert.True(await tags.IsTagSavedAsync("new-tag"));
    }

    private async Task SeedTagsAsync()
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO saved_tags(TagName, Invalid) VALUES
              ('beta',0), ('Alpha',0), ('invalid',1), ('z''quoted',0)
            """;
        await command.ExecuteNonQueryAsync();
    }

}
