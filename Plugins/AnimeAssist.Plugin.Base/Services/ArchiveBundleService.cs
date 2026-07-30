using AniMeido.Plugin.Base.Models;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed class ArchiveBundleService
{
    private const int BundleSchemaVersion = 1;
    private readonly ExportService _export;
    private readonly ArchiveService _archive;
    private readonly BackupService _backup;

    public ArchiveBundleService(
        ExportService export,
        ArchiveService archive,
        BackupService backup)
    {
        _export = export;
        _archive = archive;
        _backup = backup;
    }

    public async Task ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var temporaryPath = destinationPath + ".tmp";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        try
        {
            var screenshots = (await _archive.GetScreenshotsAsync(
                cancellationToken: cancellationToken))
                .Where(item => item.FileExists)
                .ToArray();
            var bundleScreenshots = new List<BundleScreenshot>();
            foreach (var item in screenshots)
            {
                bundleScreenshots.Add(new BundleScreenshot(
                    item,
                    $"screenshots/{item.ScreenshotId}.png",
                    await _archive.GetScreenshotTagsAsync(
                        item.ScreenshotId,
                        cancellationToken)));
            }
            var metadata = new ArchiveBundleMetadata(
                BundleSchemaVersion,
                DateTimeOffset.UtcNow,
                bundleScreenshots);
            var baseJson = await _export.ExportAsync();
            var metadataJson = JsonSerializer.Serialize(
                metadata,
                JsonOptions);
            var hashes = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            await using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                using var archive = new ZipArchive(
                    file,
                    ZipArchiveMode.Create,
                    leaveOpen: true);
                hashes["data.json"] = await WriteTextEntryAsync(
                    archive,
                    "data.json",
                    baseJson,
                    cancellationToken);
                hashes["screenshots.json"] = await WriteTextEntryAsync(
                    archive,
                    "screenshots.json",
                    metadataJson,
                    cancellationToken);
                foreach (var screenshot in metadata.Screenshots)
                {
                    var entry = archive.CreateEntry(
                        screenshot.ArchivePath,
                        CompressionLevel.Optimal);
                    await using (var input = File.OpenRead(
                        screenshot.Metadata.FilePath))
                    {
                        hashes[screenshot.ArchivePath] =
                            Convert.ToHexString(
                                await SHA256.HashDataAsync(
                                    input,
                                    cancellationToken));
                    }

                    await using var output = entry.Open();
                    await using var source = File.OpenRead(
                        screenshot.Metadata.FilePath);
                    await source.CopyToAsync(output, cancellationToken);
                }

                var manifest = string.Join(
                    '\n',
                    hashes.Select(pair =>
                        $"{pair.Value}  {pair.Key}")) + "\n";
                _ = await WriteTextEntryAsync(
                    archive,
                    "sha256.txt",
                    manifest,
                    cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<int> ImportAsync(
        string bundlePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        using var archive = ZipFile.OpenRead(bundlePath);
        ValidateEntryPaths(archive);
        var hashes = await ReadAndValidateManifestAsync(
            archive,
            cancellationToken);
        var dataJson = await ReadTextEntryAsync(
            archive,
            "data.json",
            cancellationToken);
        var metadataJson = await ReadTextEntryAsync(
            archive,
            "screenshots.json",
            cancellationToken);
        var metadata = JsonSerializer.Deserialize<ArchiveBundleMetadata>(
            metadataJson,
            JsonOptions)
            ?? throw new InvalidDataException("截图元数据无效。");
        if (metadata.SchemaVersion != BundleSchemaVersion)
        {
            throw new InvalidDataException("不支持此完整档案版本。");
        }
        if (metadata.Screenshots
            .GroupBy(item => item.Metadata.ScreenshotId, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("截图元数据包含重复 ID。");
        }

        _ = ExportService.Preview(dataJson)
            ?? throw new InvalidDataException("基础 JSON 数据无效。");
        var settings = await _archive.GetScreenshotSettingsAsync(
            cancellationToken);
        var existing = (await _archive.GetScreenshotsAsync(
            cancellationToken: cancellationToken)).ToDictionary(
                item => item.ScreenshotId,
                StringComparer.Ordinal);
        foreach (var item in metadata.Screenshots)
        {
            if (!hashes.TryGetValue(item.ArchivePath, out var hash)
                || !string.Equals(
                    hash,
                    item.Metadata.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"截图 {item.Metadata.ScreenshotId} 的哈希清单不一致。");
            }

            if (existing.TryGetValue(
                    item.Metadata.ScreenshotId,
                    out var local)
                && !string.Equals(
                    local.Sha256,
                    item.Metadata.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"截图 {item.Metadata.ScreenshotId} 与本地记录冲突。");
            }
        }

        var backupPath = await _backup.BackupAsync();
        var copiedFiles = new List<string>();
        try
        {
            await _export.ImportAsync(dataJson);
            var imported = new List<AnimeScreenshot>();
            foreach (var item in metadata.Screenshots)
            {
                if (existing.ContainsKey(item.Metadata.ScreenshotId))
                {
                    continue;
                }

                var localTime = item.Metadata.CapturedAt.ToLocalTime();
                var directory = Path.Combine(
                    settings.RootDirectory,
                    localTime.ToString("yyyy"),
                    localTime.ToString("MM"));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(
                    directory,
                    $"{item.Metadata.ScreenshotId}.png");
                var temporaryPath = path + ".tmp";
                var entry = GetRequiredEntry(archive, item.ArchivePath);
                await using (var input = entry.Open())
                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await input.CopyToAsync(output, cancellationToken);
                }
                File.Move(temporaryPath, path, overwrite: false);
                copiedFiles.Add(path);
                imported.Add(item.Metadata with
                {
                    FilePath = path,
                    FileExists = true,
                });
            }

            await _archive.ImportScreenshotsAsync(
                imported,
                cancellationToken);
            foreach (var item in metadata.Screenshots)
            {
                await _archive.AddScreenshotTagsAsync(
                    [item.Metadata.ScreenshotId],
                    item.Tags,
                    cancellationToken);
            }
            return imported.Count;
        }
        catch
        {
            await _backup.RestoreAsync(backupPath, CancellationToken.None);
            foreach (var path in copiedFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            throw;
        }
    }

    private static async Task<string> WriteTextEntryAsync(
        ZipArchive archive,
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, cancellationToken);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static async Task<string> ReadTextEntryAsync(
        ZipArchive archive,
        string path,
        CancellationToken cancellationToken)
    {
        var entry = GetRequiredEntry(archive, path);
        await using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<string, string>>
        ReadAndValidateManifestAsync(
            ZipArchive archive,
            CancellationToken cancellationToken)
    {
        var manifest = await ReadTextEntryAsync(
            archive,
            "sha256.txt",
            cancellationToken);
        var hashes = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var line in manifest.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64)
            {
                throw new InvalidDataException("SHA-256 清单格式无效。");
            }

            var hash = line[..separator];
            var path = line[(separator + 2)..];
            hashes.Add(path, hash);
        }

        foreach (var pair in hashes)
        {
            var entry = GetRequiredEntry(archive, pair.Key);
            await using var stream = entry.Open();
            var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(
                actual,
                pair.Value,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"档案文件 {pair.Key} 已损坏。");
            }
        }

        return hashes;
    }

    private static void ValidateEntryPaths(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.StartsWith("/", StringComparison.Ordinal)
                || Path.IsPathRooted(path)
                || path.Split('/').Any(part => part == ".."))
            {
                throw new InvalidDataException(
                    $"ZIP 包含不安全路径：{entry.FullName}");
            }
        }
    }

    private static ZipArchiveEntry GetRequiredEntry(
        ZipArchive archive,
        string path)
        => archive.GetEntry(path)
            ?? throw new InvalidDataException($"ZIP 缺少 {path}。");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed record ArchiveBundleMetadata(
        int SchemaVersion,
        DateTimeOffset ExportedAt,
        IReadOnlyList<BundleScreenshot> Screenshots);

    private sealed record BundleScreenshot(
        AnimeScreenshot Metadata,
        string ArchivePath,
        IReadOnlyList<string> Tags);
}
