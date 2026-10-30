using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ArchiveReviewExporterTests : DbTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_KeepsDesignAndOptionalPlayback(bool includePlayback)
    {
        await RunProductionMigrationAsync();
        var archive = new ArchiveService(DbFactory);
        await archive.UpsertArchiveAsync(42, "作品", 8, "感想");
        var occurredAt = DateTimeOffset.Now;
        await archive.AddEntryAsync(42, occurredAt, null, "<script>alert(1)</script>");
        var path = Path.Combine(Paths.BackupDirectory, "review.html");
        Directory.CreateDirectory(Paths.BackupDirectory);
        await new ArchiveReviewExporter(archive).ExportAsync(path, occurredAt.LocalDateTime.Year, includePlayback);

        var html = await File.ReadAllTextAsync(path);
        Assert.Contains("<!doctype html>", html);
        Assert.Contains("新增档案", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Equal(includePlayback, html.Contains("这一年的截图", StringComparison.Ordinal));
        Assert.Equal(includePlayback, html.Contains("估算观看时长", StringComparison.Ordinal));
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
                .ExportAsync(path, DateTime.Now.Year, false, new CancellationToken(true)));
        Assert.Equal("previous", await File.ReadAllTextAsync(path));
    }
}
