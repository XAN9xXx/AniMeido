using AniMeido.App.Services;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AniMeido.Tests;

public sealed partial class ArchiveBundleRoundTripTests
{
    [Fact]
    public async Task Export_OmitsScreenshotDisplayTextAndPreservesRawMetadataRoundTrip()
    {
        await RunProductionMigrationAsync();
        var source = CreateServices(DbFactory, Paths);
        await SeedAsync(source.Archive, DbFactory, "metadata", largeSecond: false);
        var screenshots = await source.Archive.GetScreenshotsAsync();
        var bundlePath = Path.Combine(Paths.RootDirectory, "raw-metadata.zip");

        await source.Bundle.ExportAsync(bundlePath);

        using (var zip = ZipFile.OpenRead(bundlePath))
        {
            var expectedPaths = screenshots.Select(item => $"screenshots/{item.ScreenshotId}.png")
                .Concat(["data.json", "screenshots.json", "sha256.txt"])
                .Order(StringComparer.Ordinal);
            Assert.Equal(expectedPaths, zip.Entries.Select(item => item.FullName).Order(StringComparer.Ordinal));
            await using var input = Assert.IsType<ZipArchiveEntry>(zip.GetEntry("screenshots.json")).Open();
            using var json = await JsonDocument.ParseAsync(input);
            var metadata = json.RootElement;
            Assert.Equal(2, metadata.GetProperty("schemaVersion").GetInt32());
            Assert.NotEqual(default, metadata.GetProperty("exportedAt").GetDateTimeOffset());
            Assert.Equal(new[] { "exportedAt", "schemaVersion", "screenshots" },
                metadata.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
            var exported = metadata.GetProperty("screenshots").EnumerateArray().ToArray();
            Assert.Equal(screenshots.Count, exported.Length);
            foreach (var original in screenshots)
            {
                var item = Assert.Single(exported, item =>
                    item.GetProperty("metadata").GetProperty("screenshotId").GetString() == original.ScreenshotId);
                Assert.Equal(new[] { "archivePath", "metadata", "tags" },
                    item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal($"screenshots/{original.ScreenshotId}.png", item.GetProperty("archivePath").GetString());
                Assert.Equal(await source.Archive.GetScreenshotTagsAsync(original.ScreenshotId),
                    item.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray());
                var raw = item.GetProperty("metadata");
                AssertRawScreenshotMetadata(original, raw);
                Assert.False(raw.TryGetProperty("reviewTitle", out _));
                Assert.False(raw.TryGetProperty("reviewCaption", out _));
                Assert.False(raw.TryGetProperty("captureClockText", out _));
                Assert.Equal(14, raw.EnumerateObject().Count());
            }
        }

        var target = await ImportMetadataBundleAsync(bundlePath);
        await AssertMetadataRoundTripAsync(source.Archive, DbFactory, target.Archive, target.Factory, target.Root);
    }

    [Fact]
    public async Task Import_LegacyScreenshotDisplayTextKeepsSameRawRecordsAsCurrentBundle()
    {
        await RunProductionMigrationAsync();
        var source = CreateServices(DbFactory, Paths);
        await SeedAsync(source.Archive, DbFactory, "legacy-metadata", largeSecond: false);
        var screenshots = await source.Archive.GetScreenshotsAsync();
        var legacyScreenshots = new JsonArray();
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["data.json"] = Encoding.UTF8.GetBytes(await ExportMetadataDataAsync(DbFactory)),
        };
        foreach (var screenshot in screenshots)
        {
            var archivePath = $"screenshots/{screenshot.ScreenshotId}.png";
            var tags = new JsonArray();
            foreach (var tag in await source.Archive.GetScreenshotTagsAsync(screenshot.ScreenshotId))
                tags.Add(tag);
            legacyScreenshots.Add(new JsonObject
            {
                ["metadata"] = new JsonObject
                {
                    ["screenshotId"] = screenshot.ScreenshotId,
                    ["filePath"] = screenshot.FilePath,
                    ["sha256"] = screenshot.Sha256,
                    ["capturedAt"] = JsonValue.Create(screenshot.CapturedAt),
                    ["windowTitle"] = screenshot.WindowTitle,
                    ["processName"] = screenshot.ProcessName,
                    ["width"] = screenshot.Width,
                    ["height"] = screenshot.Height,
                    ["animeId"] = screenshot.AnimeId,
                    ["animeTitle"] = screenshot.AnimeTitle,
                    ["episodeNumber"] = screenshot.EpisodeNumber,
                    ["playbackPositionSeconds"] = screenshot.PlaybackPositionSeconds,
                    ["contextNote"] = screenshot.ContextNote,
                    ["fileExists"] = screenshot.FileExists,
                    ["reviewTitle"] = "旧版显示标题",
                    ["reviewCaption"] = "旧版显示说明",
                    ["captureClockText"] = "旧版显示时间",
                },
                ["archivePath"] = archivePath,
                ["tags"] = tags,
            });
            entries[archivePath] = await ReadBytesAsync(screenshot.FilePath);
        }
        entries["screenshots.json"] = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = 2,
            ["exportedAt"] = JsonValue.Create(Captured),
            ["screenshots"] = legacyScreenshots,
        }.ToJsonString());
        var legacyPath = Path.Combine(Paths.RootDirectory, "explicit-legacy-metadata.zip");
        using (var zip = ZipFile.Open(legacyPath, ZipArchiveMode.Create))
        {
            foreach (var entry in entries)
            {
                await using var output = zip.CreateEntry(entry.Key).Open();
                await output.WriteAsync(entry.Value);
            }
            await using var manifest = new StreamWriter(zip.CreateEntry("sha256.txt").Open());
            foreach (var entry in entries.OrderBy(item => item.Key, StringComparer.Ordinal))
                await manifest.WriteAsync($"{Convert.ToHexString(SHA256.HashData(entry.Value))}  {entry.Key}\n");
        }
        var currentPath = Path.Combine(Paths.RootDirectory, "current-metadata.zip");
        await source.Bundle.ExportAsync(currentPath);

        var legacyTarget = await ImportMetadataBundleAsync(legacyPath);
        var currentTarget = await ImportMetadataBundleAsync(currentPath);

        await AssertMetadataRoundTripAsync(source.Archive, DbFactory,
            legacyTarget.Archive, legacyTarget.Factory, legacyTarget.Root);
        await AssertMetadataRoundTripAsync(currentTarget.Archive, currentTarget.Factory,
            legacyTarget.Archive, legacyTarget.Factory, legacyTarget.Root);
    }

    private static void AssertRawScreenshotMetadata(AnimeScreenshot expected, JsonElement actual)
    {
        Assert.Equal(expected.ScreenshotId, actual.GetProperty("screenshotId").GetString());
        Assert.Equal(expected.FilePath, actual.GetProperty("filePath").GetString());
        Assert.Equal(expected.Sha256, actual.GetProperty("sha256").GetString());
        Assert.Equal(expected.CapturedAt, actual.GetProperty("capturedAt").GetDateTimeOffset());
        Assert.Equal(expected.WindowTitle, actual.GetProperty("windowTitle").GetString());
        Assert.Equal(expected.ProcessName, actual.GetProperty("processName").GetString());
        Assert.Equal(expected.Width, actual.GetProperty("width").GetInt32());
        Assert.Equal(expected.Height, actual.GetProperty("height").GetInt32());
        Assert.Equal(expected.AnimeId, actual.GetProperty("animeId").GetInt32());
        Assert.Equal(expected.AnimeTitle, actual.GetProperty("animeTitle").GetString());
        Assert.Equal(expected.EpisodeNumber, actual.GetProperty("episodeNumber").GetInt32());
        Assert.Equal(expected.PlaybackPositionSeconds, actual.GetProperty("playbackPositionSeconds").GetDouble());
        Assert.Equal(expected.ContextNote, actual.GetProperty("contextNote").GetString());
        Assert.Equal(expected.FileExists, actual.GetProperty("fileExists").GetBoolean());
    }

    private async Task<(ArchiveService Archive, SqliteConnectionFactory Factory, string Root)>
        ImportMetadataBundleAsync(string bundlePath)
    {
        var paths = CreateAdditionalPaths();
        var factory = new SqliteConnectionFactory(paths);
        await new DatabaseService(factory, paths).InitializeAsync();
        var target = CreateServices(factory, paths);
        var root = Path.Combine(paths.RootDirectory, "imported-screenshots");
        await target.Archive.SaveScreenshotSettingsAsync(new ScreenshotSettings(true, root, false, false));
        Assert.Equal(2, await target.Bundle.ImportAsync(bundlePath));
        return (target.Archive, factory, root);
    }

    private static Task<string> ExportMetadataDataAsync(SqliteConnectionFactory factory)
        => new ExportService(factory).ExportAsync();

    private static async Task AssertMetadataRoundTripAsync(
        ArchiveService source, SqliteConnectionFactory sourceFactory,
        ArchiveService target, SqliteConnectionFactory targetFactory, string targetRoot)
    {
        var originalData = JsonNode.Parse(await ExportMetadataDataAsync(sourceFactory))!.AsObject();
        var importedData = JsonNode.Parse(await ExportMetadataDataAsync(targetFactory))!.AsObject();
        foreach (var data in new[] { originalData, importedData })
        {
            data.Remove("exportedAt");
            // 导入沿用当地时区格式；比较同一时刻，而不是时区写法。
            foreach (var entry in data["tracking"]!.AsArray())
            {
                var timestamp = DateTimeOffset.Parse(entry!["updatedAt"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture);
                entry["updatedAt"] = timestamp.ToUniversalTime().ToString("O");
            }
            // 标签按名称合并，目标库的本地自增编号可以不同。
            var tables = data["p4Tables"]!.AsObject();
            var tags = tables["personal_tags"]!.AsArray();
            var tagNames = tags.ToDictionary(
                tag => tag!["TagId"]!.GetValue<string>(),
                tag => tag!["Name"]!.GetValue<string>());
            foreach (var tag in tags)
                tag!["TagId"] = tagNames[tag["TagId"]!.GetValue<string>()];
            foreach (var link in tables["anime_personal_tags"]!.AsArray())
                link!["TagId"] = tagNames[link["TagId"]!.GetValue<string>()];
        }
        Assert.Equal(originalData.Select(item => item.Key), importedData.Select(item => item.Key));
        foreach (var field in originalData)
            Assert.True(JsonNode.DeepEquals(field.Value, importedData[field.Key]),
                $"字段 {field.Key} 不同。原始：{field.Value}；导入：{importedData[field.Key]}");
        Assert.Equal(await source.GetArchiveAsync(17), await target.GetArchiveAsync(17));
        Assert.Equal(await source.GetEntriesAsync(17), await target.GetEntriesAsync(17));
        Assert.Equal(await source.GetAnimeTagsAsync(17), await target.GetAnimeTagsAsync(17));
        Assert.Equal(await new TrackingService(sourceFactory).GetStatusAsync(17),
            await new TrackingService(targetFactory).GetStatusAsync(17));
        Assert.Equal(await new SavedTagService(sourceFactory).GetAllSavedTagsAsync(),
            await new SavedTagService(targetFactory).GetAllSavedTagsAsync());
        var originals = await source.GetScreenshotsAsync();
        var imported = await target.GetScreenshotsAsync();
        Assert.Equal(originals.Count, imported.Count);
        foreach (var original in originals)
        {
            var copy = Assert.Single(imported, item => item.ScreenshotId == original.ScreenshotId);
            Assert.Equal(ScreenshotDestination(targetRoot, original.ScreenshotId, original.CapturedAt), copy.FilePath);
            Assert.Equal(original with { FilePath = copy.FilePath }, copy);
            Assert.Equal(await ReadBytesAsync(original.FilePath), await ReadBytesAsync(copy.FilePath));
            Assert.Equal(await source.GetScreenshotTagsAsync(original.ScreenshotId),
                await target.GetScreenshotTagsAsync(copy.ScreenshotId));
        }
    }
}
