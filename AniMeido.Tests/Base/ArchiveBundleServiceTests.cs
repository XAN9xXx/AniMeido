using AniMeido.Plugin.Base.Services;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class ArchiveBundleServiceTests : DbTestBase
{
    [Fact]
    public async Task Export_IncludesScreenshotBytesAndTagsWithMatchingManifest()
    {
        await RunProductionMigrationAsync();
        var path = Path.Combine(Paths.BackupDirectory, "complete.zip");
        var imagePath = Path.Combine(Paths.BackupDirectory, "image.png");
        Directory.CreateDirectory(Paths.BackupDirectory);
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        await File.WriteAllBytesAsync(imagePath, bytes);
        var archive = new ArchiveService(DbFactory);
        var screenshot = new AniMeido.Plugin.Base.Models.AnimeScreenshot(
            "image", imagePath, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow,
            "window", "process", 1, 1, null, null, null, null, "note", true);
        await archive.InsertScreenshotAsync(screenshot);
        await archive.AddScreenshotTagsAsync(["image"], ["tag"]);
        var export = new ExportService(DbFactory);
        var service = new ArchiveBundleService(export, archive, new BackupService(DbFactory, Paths),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ArchiveBundleService>.Instance);

        await service.ExportAsync(path);

        using var zip = ZipFile.OpenRead(path);
        await using var image = zip.GetEntry("screenshots/image.png")!.Open();
        using var copy = new MemoryStream();
        await image.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
        using var manifest = new StreamReader(zip.GetEntry("sha256.txt")!.Open());
        Assert.Contains(screenshot.Sha256 + "  screenshots/image.png", await manifest.ReadToEndAsync());
        using var metadata = JsonDocument.Parse(zip.GetEntry("screenshots.json")!.Open());
        Assert.Equal("tag", metadata.RootElement.GetProperty("screenshots")[0].GetProperty("tags")[0].GetString());
        var snapshot = await export.ExportBundleSnapshotAsync(CancellationToken.None);
        Assert.Equal("image", Assert.Single(snapshot.Screenshots).ScreenshotId);
        Assert.Equal(["tag"], snapshot.Tags["image"]);
    }

    [Fact]
    public async Task Import_RejectsDuplicateManifestPath()
    {
        await RunProductionMigrationAsync();
        var path = Path.Combine(
            Path.GetTempPath(),
            $"AniMeido-duplicate-manifest-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(
                path,
                ZipArchiveMode.Create))
            {
                WriteEntry(archive, "data.json", "{}");
                WriteEntry(archive, "screenshots.json", "{}");
                WriteEntry(
                    archive,
                    "sha256.txt",
                    $"{new string('A', 64)}  data.json\n"
                        + $"{new string('B', 64)}  data.json\n");
            }

            var service = new ArchiveBundleService(
                new ExportService(DbFactory),
                new ArchiveService(DbFactory),
                new BackupService(DbFactory, Paths), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArchiveBundleService>.Instance);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ImportAsync(path));
            Assert.Contains("重复路径", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Import_RejectsUnsafeScreenshotId()
    {
        await RunProductionMigrationAsync();
        var path = Path.Combine(
            Path.GetTempPath(),
            $"AniMeido-unsafe-screenshot-{Guid.NewGuid():N}.zip");
        try
        {
            var export = new ExportService(DbFactory);
            var dataJson = await export.ExportAsync();
            var screenshot = new
            {
                metadata = new
                {
                    screenshotId = "../outside",
                    filePath = "ignored.png",
                    sha256 = new string('A', 64),
                    capturedAt = DateTimeOffset.UtcNow,
                    windowTitle = "Window",
                    processName = "Process",
                    width = 1,
                    height = 1,
                    animeId = (int?)null,
                    animeTitle = (string?)null,
                    episodeNumber = (int?)null,
                    playbackPositionSeconds = (double?)null,
                    contextNote = "",
                    fileExists = true,
                },
                archivePath = "screenshots/../outside.png",
                tags = Array.Empty<string>(),
            };
            var metadataJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                exportedAt = DateTimeOffset.UtcNow,
                screenshots = new[] { screenshot },
            });
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "data.json", dataJson);
                WriteEntry(archive, "screenshots.json", metadataJson);
                WriteEntry(
                    archive,
                    "sha256.txt",
                    $"{Hash(dataJson)}  data.json\n"
                        + $"{Hash(metadataJson)}  screenshots.json\n");
            }

            var service = new ArchiveBundleService(
                export,
                new ArchiveService(DbFactory),
                new BackupService(DbFactory, Paths), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArchiveBundleService>.Instance);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ImportAsync(path));
            Assert.Contains("不安全 ID", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Import_RejectsUnhashedRequiredMetadata()
    {
        await RunProductionMigrationAsync();
        var path = Path.Combine(
            Path.GetTempPath(),
            $"AniMeido-unhashed-metadata-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "data.json", "{}");
                WriteEntry(archive, "screenshots.json", "{}");
                WriteEntry(
                    archive,
                    "sha256.txt",
                    $"{Hash("{}")}  data.json\n");
            }

            var service = new ArchiveBundleService(
                new ExportService(DbFactory),
                new ArchiveService(DbFactory),
                new BackupService(DbFactory, Paths), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArchiveBundleService>.Instance);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ImportAsync(path));
            Assert.Contains("缺少 screenshots.json", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(content)));
}
