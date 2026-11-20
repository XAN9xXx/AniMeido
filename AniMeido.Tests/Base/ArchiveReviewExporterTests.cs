using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ArchiveReviewExporterTests : DbTestBase
{
    [Fact]
    public async Task Export_IncludesLocalScreenshotAndWatchStatistics()
    {
        await RunProductionMigrationAsync();
        var archive = new ArchiveService(DbFactory);
        await archive.UpsertArchiveAsync(42, "作品", 8, "感想");
        var occurredAt = DateTimeOffset.Now;
        await archive.AddEntryAsync(42, occurredAt, null, "<script>alert(1)</script>");
        var path = Path.Combine(Paths.BackupDirectory, "review.html");
        Directory.CreateDirectory(Paths.BackupDirectory);
        var imagePath = Path.Combine(Paths.BackupDirectory, "shot.png");
        byte[] image = [137, 80, 78, 71];
        await File.WriteAllBytesAsync(imagePath, image);
        await archive.InsertScreenshotAsync(new("shot", imagePath, "", occurredAt, "", "", 1, 1, 42, "作品", 3, null, "本地截图", true));
        await new ArchiveReviewExporter(archive).ExportAsync(path, occurredAt.LocalDateTime.Year);

        var html = await File.ReadAllTextAsync(path);
        Assert.Contains("<!doctype html>", html);
        Assert.Contains("新增档案", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(image), html);
        Assert.Contains("本地截图", html);
        Assert.Contains("估算观看时长", html);
        Assert.Empty(Directory.GetFiles(Paths.BackupDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Export_CancelledBeforeStartPreservesExistingOutput()
    {
        await RunProductionMigrationAsync();
        var path = Path.Combine(Paths.BackupDirectory, "review.html");
        Directory.CreateDirectory(Paths.BackupDirectory);
        await File.WriteAllTextAsync(path, "previous");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ArchiveReviewExporter(new ArchiveService(DbFactory))
                .ExportAsync(path, DateTime.Now.Year, new CancellationToken(true)));
        Assert.Equal("previous", await File.ReadAllTextAsync(path));
    }
}
