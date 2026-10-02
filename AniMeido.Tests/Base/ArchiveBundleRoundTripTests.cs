using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class ArchiveBundleRoundTripTests : DbTestBase
{
    [Fact]
    public async Task Bundle_RoundTripPreservesPersonalDataScreenshotBytesAndTags()
    {
        await RunProductionMigrationAsync();
        var source = CreateServices(DbFactory, Paths);
        await SeedAsync(source.Archive, DbFactory, "source", largeSecond: false);
        var bundle = Path.Combine(Path.GetDirectoryName(DbPath)!, "round-trip.zip");
        await source.Bundle.ExportAsync(bundle);
        var targetPaths = new MockAppDataPaths();
        var targetFactory = new SqliteConnectionFactory(targetPaths);
        await new AniMeido.App.Services.DatabaseService(targetFactory, targetPaths).InitializeAsync();
        var target = CreateServices(targetFactory, targetPaths);
        var targetRoot = Path.Combine(Path.GetDirectoryName(targetPaths.DatabasePath)!, "screenshots");
        await target.Archive.SaveScreenshotSettingsAsync(new ScreenshotSettings(true, targetRoot, false, false));

        Assert.Equal(2, await target.Bundle.ImportAsync(bundle));

        Assert.Equal(AnimeTrackingStatus.Watching, await new TrackingService(targetFactory).GetStatusAsync(17));
        var restored = await target.Archive.GetArchiveAsync(17);
        Assert.NotNull(restored);
        Assert.Equal("往返作品", restored.TitleSnapshot);
        Assert.Equal(8.5, restored.PersonalRating);
        Assert.Equal("完整笔记", restored.SummaryNote);
        Assert.Equal(new[] { "个人标签", "第二标签" }, await target.Archive.GetAnimeTagsAsync(17));
        var entry = Assert.Single(await target.Archive.GetEntriesAsync(17));
        Assert.Equal("第3集感想", entry.Body);
        Assert.Equal(3, entry.EpisodeNumber);
        Assert.Equal(new[] { "科幻" }, await new SavedTagService(targetFactory).GetAllSavedTagsAsync());
        var originalScreenshots = await source.Archive.GetScreenshotsAsync();
        var screenshots = await target.Archive.GetScreenshotsAsync();
        Assert.Equal(2, screenshots.Count);
        foreach (var original in originalScreenshots)
        {
            var copy = Assert.Single(screenshots, item => item.ScreenshotId == original.ScreenshotId);
            Assert.True(copy.FileExists);
            Assert.StartsWith(targetRoot, copy.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(await ReadBytesAsync(original.FilePath), await ReadBytesAsync(copy.FilePath));
            Assert.Equal(original with { FilePath = copy.FilePath }, copy);
            Assert.Equal(await source.Archive.GetScreenshotTagsAsync(original.ScreenshotId),
                await target.Archive.GetScreenshotTagsAsync(copy.ScreenshotId));
        }
        Assert.Empty(Directory.GetFiles(targetRoot, "*.tmp", SearchOption.AllDirectories));
        await using var connection = await targetFactory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
    }

    [Fact]
    public Task Import_SecondScreenshotWriteFailureRestoresDatabaseAndRemovesCopiedFiles()
        => AssertRollbackAsync(cancel: false, unrelatedWrite: false);

    [Fact]
    public Task Import_CancelAfterFirstScreenshotRestoresDatabaseAndRemovesCopiedFiles()
        => AssertRollbackAsync(cancel: true, unrelatedWrite: false);

    [Fact]
    public Task Import_FailureAlsoRevertsUnrelatedWriteCommittedDuringImport()
        => AssertRollbackAsync(cancel: false, unrelatedWrite: true);

    private async Task AssertRollbackAsync(bool cancel, bool unrelatedWrite)
    {
        await RunProductionMigrationAsync();
        var sourcePaths = new MockAppDataPaths();
        var sourceFactory = new SqliteConnectionFactory(sourcePaths);
        await new AniMeido.App.Services.DatabaseService(sourceFactory, sourcePaths).InitializeAsync();
        var source = CreateServices(sourceFactory, sourcePaths);
        await SeedAsync(source.Archive, sourceFactory, "incoming", largeSecond: true);
        var bundle = Path.Combine(Path.GetDirectoryName(DbPath)!, "rollback.zip");
        await source.Bundle.ExportAsync(bundle);
        var target = CreateServices(DbFactory, Paths);
        var root = Path.Combine(Path.GetDirectoryName(DbPath)!, "target-screenshots");
        await target.Archive.SaveScreenshotSettingsAsync(new ScreenshotSettings(true, root, false, false));
        await new TrackingService(DbFactory).SetStatusAsync(80, AnimeTrackingStatus.Completed);
        await target.Archive.UpsertArchiveAsync(80, "原有作品", 9, "原有笔记");
        var originalPath = Path.Combine(root, "existing.png");
        Directory.CreateDirectory(root);
        var originalBytes = Png(0, 0, 255);
        await File.WriteAllBytesAsync(originalPath, originalBytes);
        await target.Archive.InsertScreenshotAsync(Screenshot("existing", originalPath, originalBytes, Captured.AddDays(-10), 80));
        await target.Archive.AddScreenshotTagsAsync(["existing"], ["原有截图标签"]);
        var before = await SnapshotAsync();
        var firstPath = ScreenshotDestination(root, "incoming-first", Captured);
        var secondPath = ScreenshotDestination(root, "incoming-second", Captured.AddDays(-1));
        using var cancellation = new CancellationTokenSource();
        var intercepted = false;
        var externalWriteCommitted = false;

        // 仅控制此调用的异步续体；第二张 PNG 的合法辅助块保证复制经过多次异步 I/O。
        // 不依赖 sleep 或监视器通知时序，在第一张已移动、第二张尚未完成的续体边界注入故障。
        var import = RunWithContinuationHookAsync(() => target.Bundle.ImportAsync(bundle, cancellation.Token), () =>
        {
            if (intercepted || !File.Exists(firstPath)) return;
            intercepted = true;
            if (unrelatedWrite)
            {
                using var connection = DbFactory.CreateConnection();
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO config(Key, Value) VALUES('other-window-write', 'committed')";
                Assert.Equal(1, command.ExecuteNonQuery());
                command.CommandText = "SELECT Value FROM config WHERE Key = 'other-window-write'";
                Assert.Equal("committed", command.ExecuteScalar());
                externalWriteCommitted = true;
            }
            if (cancel) cancellation.Cancel();
            else Directory.CreateDirectory(secondPath); // Windows：File.Move 不能覆盖同名目录。
        });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import.WaitAsync(TimeSpan.FromSeconds(10)));
        else
            await Assert.ThrowsAnyAsync<IOException>(() => import.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(intercepted, "故障必须在第一张已经复制后注入，不能只验证导入前校验。");
        Assert.Equal(unrelatedWrite, externalWriteCommitted);
        Assert.Equal(before, await SnapshotAsync());
        Assert.False(File.Exists(firstPath));
        Assert.False(File.Exists(secondPath));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(originalBytes, await ReadBytesAsync(originalPath));
        Assert.Equal(new[] { "原有截图标签" }, await target.Archive.GetScreenshotTagsAsync("existing"));
        var onlyScreenshot = Assert.Single(await target.Archive.GetScreenshotsAsync());
        Assert.Equal("existing", onlyScreenshot.ScreenshotId);
        Assert.Equal(AnimeTrackingStatus.Completed, await new TrackingService(DbFactory).GetStatusAsync(80));
        Assert.Null(await new TrackingService(DbFactory).GetStatusAsync(17));
    }

    private static readonly DateTimeOffset Captured = new(2025, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static (ArchiveService Archive, ArchiveBundleService Bundle) CreateServices(SqliteConnectionFactory factory, MockAppDataPaths paths)
    {
        var archive = new ArchiveService(factory);
        return (archive, new ArchiveBundleService(new ExportService(new TrackingService(factory),
            new SavedTagService(factory), factory), archive, new BackupService(factory, paths)));
    }

    private static async Task SeedAsync(ArchiveService archive, SqliteConnectionFactory factory, string prefix, bool largeSecond)
    {
        await new TrackingService(factory).SetStatusAsync(17, AnimeTrackingStatus.Watching);
        await archive.UpsertArchiveAsync(17, "往返作品", 8.5, "完整笔记");
        await archive.SetAnimeTagsAsync(17, ["个人标签", "第二标签"]);
        await archive.AddEntryAsync(17, Captured, 3, "第3集感想");
        await new SavedTagService(factory).SaveTagAsync("科幻");
        var root = Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, "source-pictures");
        Directory.CreateDirectory(root);
        foreach (var (suffix, time, bytes) in new[]
        {
            ("first", Captured, Png(255, 0, 0)),
            ("second", Captured.AddDays(-1), Png(0, 255, 0, largeSecond ? 2 * 1024 * 1024 : 0)),
        })
        {
            var id = $"{prefix}-{suffix}";
            var path = Path.Combine(root, $"{id}.png");
            await File.WriteAllBytesAsync(path, bytes);
            await archive.InsertScreenshotAsync(Screenshot(id, path, bytes, time, 17));
            await archive.AddScreenshotTagsAsync([id], ["截图标签", suffix]);
        }
    }

    private static AnimeScreenshot Screenshot(string id, string path, byte[] bytes, DateTimeOffset time, int animeId) => new(
        id, path, Convert.ToHexString(SHA256.HashData(bytes)), time, "fixture-window", "fixture-process", 1, 1,
        animeId, "截图作品", 3, 42.5, "截图备注", true);

    private static string ScreenshotDestination(string root, string id, DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return Path.Combine(root, local.ToString("yyyy"), local.ToString("MM"), $"{id}.png");
    }

    private async Task<string> SnapshotAsync()
    {
        using var connection = await DbFactory.OpenAsync();
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var names = new List<string>();
        using (var reader = await tables.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        var result = new List<string>();
        foreach (var name in names)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values.Select(value => value is DBNull ? null : value)));
            }
            result.Add(name + ":" + string.Join("|", rows.Order(StringComparer.Ordinal)));
        }
        return string.Join("\n", result);
    }

    private static async Task<byte[]> ReadBytesAsync(string path)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }

    // 最小合法 1x1 RGBA PNG，独立生成 CRC/IDAT；不把随意字节冒充图片。
    private static byte[] Png(byte red, byte green, byte blue, int commentLength = 0)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 1);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8; header[9] = 6;
        Chunk("IHDR", header);
        using var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write([0, red, green, blue, 255]);
        Chunk("IDAT", pixels.ToArray());
        if (commentLength > 0)
        {
            var comment = Enumerable.Repeat((byte)'x', commentLength).ToArray();
            Encoding.ASCII.GetBytes("Comment\0").CopyTo(comment, 0);
            Chunk("tEXt", comment);
        }
        Chunk("IEND", []);
        return output.ToArray();
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);
            var bytes = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            output.Write(bytes);
            uint crc = uint.MaxValue;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
            output.Write(length);
        }
    }

    private static Task RunWithContinuationHookAsync(Func<Task> operation, Action beforeContinuation) => Task.Run(() =>
    {
        using var context = new HookContext(beforeContinuation);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var pending = operation();
            while (!pending.IsCompleted) context.RunOne();
            pending.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private sealed class HookContext(Action hook) : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public void RunOne()
        {
            if (!_queue.TryTake(out var work, TimeSpan.FromSeconds(5))) throw new TimeoutException("Import continuation stalled");
            hook();
            work.Callback(work.State);
        }
        public void Dispose() => _queue.Dispose();
    }
}
