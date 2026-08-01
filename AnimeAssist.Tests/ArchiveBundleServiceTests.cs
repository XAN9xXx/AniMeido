using AniMeido.Plugin.Base.Services;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class ArchiveBundleServiceTests : DbTestBase
{
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
                new ExportService(
                    new TrackingService(DbFactory),
                    new SavedTagService(DbFactory),
                    DbFactory),
                new ArchiveService(DbFactory),
                new BackupService(DbFactory, Paths));

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
            var export = new ExportService(
                new TrackingService(DbFactory),
                new SavedTagService(DbFactory),
                DbFactory);
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
                new BackupService(DbFactory, Paths));
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
                new ExportService(
                    new TrackingService(DbFactory),
                    new SavedTagService(DbFactory),
                    DbFactory),
                new ArchiveService(DbFactory),
                new BackupService(DbFactory, Paths));
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
