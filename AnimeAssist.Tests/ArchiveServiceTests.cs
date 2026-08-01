using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ArchiveServiceTests : DbTestBase
{
    [Fact]
    public async Task Archive_RatingAndTagsRoundTrip()
    {
        await RunProductionMigrationAsync();
        var service = new ArchiveService(DbFactory);

        await service.UpsertArchiveAsync(
            42,
            "测试番剧",
            8.5,
            "概要");
        await service.SetAnimeTagsAsync(
            42,
            ["治愈", "治愈", "科幻"]);

        var archive = await service.GetArchiveAsync(42);
        var tags = await service.GetAnimeTagsAsync(42);

        Assert.NotNull(archive);
        Assert.Equal(8.5, archive.PersonalRating);
        Assert.Equal("概要", archive.SummaryNote);
        Assert.Equal(
            new[] { "治愈", "科幻" }.Order(),
            tags.Order());

        await service.AddEntryAsync(
            42,
            DateTimeOffset.UtcNow,
            1,
            "初次感想");
        var entry = Assert.Single(await service.GetEntriesAsync(42));
        await service.UpdateEntryAsync(
            entry.EntryId,
            entry.OccurredAt,
            2,
            "修改后的感想");
        var updated = Assert.Single(await service.GetEntriesAsync(42));
        Assert.Equal(2, updated.EpisodeNumber);
        Assert.Equal("修改后的感想", updated.Body);
        await service.DeleteEntryAsync(updated.EntryId);
        Assert.Empty(await service.GetEntriesAsync(42));
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(10.5)]
    [InlineData(8.25)]
    public async Task Archive_RejectsInvalidRating(double rating)
    {
        await RunProductionMigrationAsync();
        var service = new ArchiveService(DbFactory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.UpsertArchiveAsync(1, "测试", rating, string.Empty));
    }

    [Fact]
    public async Task ScreenshotImport_RejectsSameIdWithDifferentHash()
    {
        await RunProductionMigrationAsync();
        var service = new ArchiveService(DbFactory);
        var capturedAt = DateTimeOffset.UtcNow;
        var original = CreateScreenshot("shot", "AAA", capturedAt);
        await service.InsertScreenshotAsync(original);

        var conflicting = CreateScreenshot("shot", "BBB", capturedAt);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportScreenshotsAsync([conflicting]));
    }

    [Fact]
    public async Task ScreenshotImport_RepairsMissingFileForSameHash()
    {
        await RunProductionMigrationAsync();
        var service = new ArchiveService(DbFactory);
        var capturedAt = DateTimeOffset.UtcNow;
        var original = CreateScreenshot("shot", "AAA", capturedAt);
        await service.InsertScreenshotAsync(original);

        var replacementPath = Path.GetTempFileName();
        try
        {
            var replacement = CreateScreenshot(
                "shot",
                "AAA",
                capturedAt) with
            {
                FilePath = replacementPath,
                FileExists = true,
            };
            await service.ImportScreenshotsAsync([replacement]);

            var restored = Assert.Single(
                await service.GetScreenshotsAsync());
            Assert.Equal(replacementPath, restored.FilePath);
            Assert.True(restored.FileExists);
        }
        finally
        {
            File.Delete(replacementPath);
        }
    }

    [Fact]
    public void ShortcutGate_DeduplicatesHoldAndConcurrentAction()
    {
        var gate = new AniMeido.App.Services.ShortcutInputGate();

        Assert.True(gate.TryBegin());
        Assert.False(gate.TryBegin());
        gate.ReleaseKey();
        Assert.False(gate.TryBegin());
        gate.CompleteAction();
        gate.ReleaseKey();
        Assert.True(gate.TryBegin());
    }

    private static AnimeScreenshot CreateScreenshot(
        string id,
        string hash,
        DateTimeOffset capturedAt)
        => new(
            id,
            @"C:\missing.png",
            hash,
            capturedAt,
            "Window",
            "Process",
            1920,
            1080,
            null,
            null,
            null,
            null,
            string.Empty,
            false);
}
