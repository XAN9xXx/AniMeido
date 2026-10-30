using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ArchiveEntryInputTests : IAsyncLifetime
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UpdateEntry_InvalidEpisodeKeepsTheCompleteExistingEntry(int episodeNumber)
    {
        var archive = new ArchiveService(_database.Factory);
        var original = await CreateEntryAsync(archive);

        var error = await Record.ExceptionAsync(() => archive.UpdateEntryAsync(
            original.EntryId, OccurredAt.AddDays(1), episodeNumber, "不应保存的正文"));
        var saved = Assert.Single(await archive.GetEntriesAsync(42));

        Assert.Equal(original, saved);
        var inputError = Assert.IsType<ArgumentOutOfRangeException>(error);
        Assert.Equal("episodeNumber", inputError.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task UpdateEntry_OptionalAndPositiveEpisodesKeepTheExistingRange(int? episodeNumber)
    {
        var archive = new ArchiveService(_database.Factory);
        var original = await CreateEntryAsync(archive);
        var changedTime = OccurredAt.AddDays(1);

        await archive.UpdateEntryAsync(original.EntryId, changedTime, episodeNumber, "  修改后的正文  ");
        var saved = Assert.Single(await archive.GetEntriesAsync(42));

        Assert.Equal(original with
        {
            OccurredAt = changedTime,
            EpisodeNumber = episodeNumber,
            Body = "修改后的正文",
            UpdatedAt = saved.UpdatedAt,
        }, saved);
    }

    private static async Task<ArchiveEntry> CreateEntryAsync(ArchiveService archive)
    {
        await archive.UpsertArchiveAsync(42, "已有档案", 8.5, "保留摘要");
        await archive.AddEntryAsync(42, OccurredAt, 3, "原始正文");
        return Assert.Single(await archive.GetEntriesAsync(42));
    }
}
