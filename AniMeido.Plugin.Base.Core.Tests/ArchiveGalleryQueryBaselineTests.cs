using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class ArchiveGalleryQueryBaselineTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PersonalDatabaseFixture _database = new();
    private readonly string _note = new('图', 512);

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "Performance")]
    public async Task CompleteGalleryPreservesRowsTagsOrderAndFilePresenceAtScale(int count)
    {
        var directory = await SeedAsync(count);
        var queries = new ArchiveQueryService(new ArchiveService(_database.Factory));
        for (var round = 0; round < 3; round++)
        {
            AssertGallery(await queries.LoadScreenshotsAsync(), count, directory);
        }

        var times = new double[5];
        var allocations = new long[5];
        for (var round = 0; round < times.Length; round++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var gallery = await queries.LoadScreenshotsAsync();
            times[round] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocations[round] = GC.GetTotalAllocatedBytes(precise: true) - before;
            AssertGallery(gallery, count, directory);
        }

        output.WriteLine("Screenshots={0}; distinct paths={0}; present files={1}; tags per row=3; note chars=512.",
            count, count / 2);
        output.WriteLine("Release; warm synthetic SQLite; 3 warmups / 5 samples; setup/assertions excluded.");
        output.WriteLine("LoadScreenshotsAsync: median {0:F3} ms / {1} process allocated B; ms=[{2}]; B=[{3}]",
            times.Order().ElementAt(2), allocations.Order().ElementAt(2),
            string.Join(", ", times.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))),
            string.Join(", ", allocations));
        output.WriteLine("Run this class alone for process allocations. Presence files are empty fixtures; no image decode or UI timing is measured.");
    }

    private async Task<string> SeedAsync(int count)
    {
        var directory = Path.Combine(Path.GetDirectoryName(_database.Factory.DatabasePath)!, "gallery-presence");
        Directory.CreateDirectory(directory);
        using var connection = await _database.Factory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO personal_tags(TagId, Name) VALUES(1, 'a'), (2, 'b'), (3, 'c')";
        await command.ExecuteNonQueryAsync();
        command.CommandText = """
            INSERT INTO screenshots(
                ScreenshotId, FilePath, Sha256, CapturedAt, WindowTitle, ProcessName,
                Width, Height, AnimeId, AnimeTitle, EpisodeNumber, PlaybackPositionSeconds, ContextNote)
            VALUES(@id, @path, 'stored-fixture-value', @at, 'Window', 'Fixture',
                   1920, 1080, 42, '完整图库', 3, 90.5, @note);
            INSERT INTO screenshot_personal_tags(ScreenshotId, TagId)
            VALUES(@id, 3), (@id, 1), (@id, 2);
            """;
        var id = command.Parameters.AddWithValue("@id", string.Empty);
        var path = command.Parameters.AddWithValue("@path", string.Empty);
        var at = command.Parameters.AddWithValue("@at", string.Empty);
        command.Parameters.AddWithValue("@note", _note);
        for (var index = 1; index <= count; index++)
        {
            id.Value = $"shot-{index:D5}";
            var file = Path.Combine(directory, $"{index:D5}.fixture");
            path.Value = file;
            at.Value = CapturedAt.AddSeconds(index).ToString("O", CultureInfo.InvariantCulture);
            if (index % 2 == 0)
            {
                // 只测生产查询的文件存在性，不拿合成文件假装做了图片呈现验收。
                File.WriteAllBytes(file, []);
            }
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
        return directory;
    }

    private void AssertGallery(ArchiveScreenshotData gallery, int count, string directory)
    {
        Assert.Equal(count, gallery.Screenshots.Count);
        Assert.Equal(count, gallery.Tags.Count);
        Assert.Equal(count / 2, gallery.Screenshots.Count(item => item.FileExists));
        for (var position = 0; position < count; position++)
        {
            var index = count - position;
            var screenshot = gallery.Screenshots[position];
            Assert.Equal($"shot-{index:D5}", screenshot.ScreenshotId);
            Assert.Equal(Path.Combine(directory, $"{index:D5}.fixture"), screenshot.FilePath);
            Assert.Equal(CapturedAt.AddSeconds(index), screenshot.CapturedAt);
            Assert.Equal(index % 2 == 0, screenshot.FileExists);
            Assert.Equal("stored-fixture-value", screenshot.Sha256);
            Assert.Equal("Window", screenshot.WindowTitle);
            Assert.Equal("Fixture", screenshot.ProcessName);
            Assert.Equal(1920, screenshot.Width);
            Assert.Equal(1080, screenshot.Height);
            Assert.Equal(42, screenshot.AnimeId);
            Assert.Equal("完整图库", screenshot.AnimeTitle);
            Assert.Equal(3, screenshot.EpisodeNumber);
            Assert.Equal(90.5, screenshot.PlaybackPositionSeconds);
            Assert.Equal(_note, screenshot.ContextNote);
            Assert.Equal(["a", "b", "c"], gallery.Tags[screenshot.ScreenshotId]);
        }
    }
}
