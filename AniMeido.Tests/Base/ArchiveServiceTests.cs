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

    [Fact]
    public async Task AddEntry_MissingArchiveIsRejectedWithoutSideEffects()
    {
        await RunProductionMigrationAsync();
        var service = new ArchiveService(DbFactory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddEntryAsync(
            43,
            DateTimeOffset.UtcNow,
            1,
            "仅记录感想"));

        Assert.Contains("档案", error.Message);
        Assert.Null(await service.GetArchiveAsync(43));
        Assert.Empty(await service.GetEntriesAsync(43));
        Assert.Empty(await service.GetArchiveListAsync());
    }

    [Fact]
    public async Task AddEntry_ExistingArchiveKeepsRatingAndSummary()
    {
        await RunProductionMigrationAsync();
        var service = new ArchiveService(DbFactory);
        await service.UpsertArchiveAsync(43, "已有档案", 8.5, "原摘要");
        var before = await service.GetArchiveAsync(43);

        await service.AddEntryAsync(43, DateTimeOffset.UtcNow, 2, "新增笔记");

        Assert.Equal(before, await service.GetArchiveAsync(43));
        Assert.Equal("新增笔记", Assert.Single(await service.GetEntriesAsync(43)).Body);
    }

    [Fact]
    public async Task EnsureArchiveAndAddEntry_CommitsOrRollsBackTogether()
    {
        await RunProductionMigrationAsync();
        using var connection = await DbFactory.OpenAsync();
        using (var transaction = connection.BeginTransaction())
        {
            await ArchiveService.EnsureArchiveAndAddEntryInTransactionAsync(
                connection, transaction, "gateway-entry", 43, "网关档案", DateTimeOffset.UtcNow,
                1, "网关笔记", DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None);
            transaction.Commit();
        }
        var service = new ArchiveService(DbFactory);
        Assert.NotNull(await service.GetArchiveAsync(43));
        Assert.Equal("gateway-entry", Assert.Single(await service.GetEntriesAsync(43)).EntryId);

        using (var transaction = connection.BeginTransaction())
        {
            // 第二次追加使用冲突的 EntryId，先创建的父档案也必须回滚。
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                ArchiveService.EnsureArchiveAndAddEntryInTransactionAsync(
                    connection, transaction, "gateway-entry", 44, "应回滚档案", DateTimeOffset.UtcNow,
                    1, "失败笔记", DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None));
            transaction.Rollback();
        }
        Assert.Null(await service.GetArchiveAsync(44));
        Assert.Empty(await service.GetEntriesAsync(44));
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
