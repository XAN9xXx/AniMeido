using AniMeido.Plugin.Player.Sources.Web;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class HostWebSessionWriteFailureTests
{
    private const string ValidMetadata = """
        [
          {
            "Host": "site.example.test",
            "UserAgent": "existing-agent",
            "SourceIds": ["zeta"],
            "LastVerifiedAt": "2026-10-20T10:00:00+00:00"
          },
          {
            "Host": "other.example.test",
            "UserAgent": "other-agent",
            "SourceIds": ["other-source"],
            "LastVerifiedAt": "2026-10-19T10:00:00+00:00"
          }
        ]
        """;

    [Theory]
    [InlineData("{ broken session metadata", "record")]
    [InlineData("{ broken session metadata", "remove-one")]
    [InlineData("null", "record")]
    [InlineData("null", "remove-one")]
    [InlineData("[{\"Host\":\"site.example.test\",\"SourceIds\":null}]", "record")]
    [InlineData("[{\"Host\":\"site.example.test\",\"SourceIds\":null}]", "remove-one")]
    [InlineData("[{\"Host\":null,\"SourceIds\":[]}]", "record")]
    [InlineData("[{\"Host\":null,\"SourceIds\":[]}]", "remove-one")]
    public async Task ReadFailureRejectsWriteAndPreservesOriginalFile(string original, string operation)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(original);

        var error = await Record.ExceptionAsync(() =>
            ApplyAsync(fixture.Manager, operation, CancellationToken.None));

        // 先检查文件，避免仅以“未抛异常”掩盖旧代码已经覆盖原文的事实。
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.MetadataPath));
        Assert.NotNull(error);
        Assert.True(error is JsonException or InvalidDataException);
        Assert.Empty(Directory.EnumerateFiles(fixture.MetadataDirectory, "*.tmp"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    [Theory]
    [InlineData("{ broken session metadata")]
    [InlineData("null")]
    public async Task ExplicitClearAllResetsUnreadableMetadataAndAllowsSubsequentRecording(string original)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(original);

        await fixture.Manager.RemoveMetadataAsync(host: null, CancellationToken.None);

        using (var cleared = await fixture.ReadDocumentAsync())
        {
            Assert.Equal(JsonValueKind.Array, cleared.RootElement.ValueKind);
            Assert.Equal(0, cleared.RootElement.GetArrayLength());
        }

        await fixture.Manager.RecordVerifiedAsync(
            "new.example.test", "new-source", "new-agent", CancellationToken.None);

        using var restored = await fixture.ReadDocumentAsync();
        var item = Assert.Single(restored.RootElement.EnumerateArray());
        Assert.Equal("new.example.test", item.GetProperty("Host").GetString());
        Assert.Equal("new-agent", item.GetProperty("UserAgent").GetString());
        Assert.Equal("new-source", Assert.Single(item.GetProperty("SourceIds").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task MissingMetadataIsCreatedByFirstVerifiedSession()
    {
        using var fixture = new Fixture();

        await fixture.Manager.RecordVerifiedAsync(
            " WWW.New.Example.Test. ", "new-source", "new-agent", CancellationToken.None);

        using var document = await fixture.ReadDocumentAsync();
        var item = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("new.example.test", item.GetProperty("Host").GetString());
        Assert.Equal("new-agent", item.GetProperty("UserAgent").GetString());
        Assert.Equal("new-source", Assert.Single(item.GetProperty("SourceIds").EnumerateArray()).GetString());
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    [Fact]
    public async Task RecordingExistingHostMergesSourceIdsAndPreservesOtherHosts()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(ValidMetadata);

        await fixture.Manager.RecordVerifiedAsync(
            "www.SITE.example.test.", "alpha", userAgent: null, CancellationToken.None);
        await fixture.Manager.RecordVerifiedAsync(
            "site.example.test", "zeta", userAgent: null, CancellationToken.None);

        using var document = await fixture.ReadDocumentAsync();
        var items = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        var updated = Assert.Single(items, item => item.GetProperty("Host").GetString() == "site.example.test");
        Assert.Equal("existing-agent", updated.GetProperty("UserAgent").GetString());
        Assert.Equal(new[] { "alpha", "zeta" }, updated.GetProperty("SourceIds")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.True(updated.GetProperty("LastVerifiedAt").GetDateTimeOffset()
            > DateTimeOffset.Parse("2026-10-20T10:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture));
        AssertOtherHostUnchanged(Assert.Single(items,
            item => item.GetProperty("Host").GetString() == "other.example.test"));
    }

    [Fact]
    public async Task RemovingOneHostPreservesOtherHosts()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(ValidMetadata);

        await fixture.Manager.RemoveMetadataAsync("www.SITE.example.test.", CancellationToken.None);

        using var document = await fixture.ReadDocumentAsync();
        AssertOtherHostUnchanged(Assert.Single(document.RootElement.EnumerateArray()));
    }

    [Fact]
    public async Task SameManagerCanRecordAfterTheCallerRepairsTheFile()
    {
        using var fixture = new Fixture();
        const string broken = "{ broken session metadata";
        await fixture.SeedAsync(broken);
        var error = await Record.ExceptionAsync(() => fixture.Manager.RecordVerifiedAsync(
            "site.example.test", "alpha", "new-agent", CancellationToken.None));
        Assert.Equal(broken, await File.ReadAllTextAsync(fixture.MetadataPath));
        Assert.NotNull(error);

        await fixture.SeedAsync(ValidMetadata);
        await fixture.Manager.RecordVerifiedAsync(
            "site.example.test", "alpha", "new-agent", CancellationToken.None);

        using var document = await fixture.ReadDocumentAsync();
        var items = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        var updated = Assert.Single(items, item => item.GetProperty("Host").GetString() == "site.example.test");
        Assert.Equal("new-agent", updated.GetProperty("UserAgent").GetString());
        Assert.Equal(new[] { "alpha", "zeta" }, updated.GetProperty("SourceIds")
            .EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("remove-one")]
    [InlineData("clear-all")]
    public async Task PreCancelledWriteKeepsOriginalMetadata(string operation)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(ValidMetadata);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ApplyAsync(fixture.Manager, operation, cancellation.Token));

        Assert.Equal(ValidMetadata, await File.ReadAllTextAsync(fixture.MetadataPath));
        Assert.Empty(Directory.EnumerateFiles(fixture.MetadataDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("remove-one")]
    public async Task UnreadableMetadataRejectsWriteAndKeepsOriginalFile(string operation)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(ValidMetadata);
        Exception? error;
        using (var unreadable = new FileStream(
            fixture.MetadataPath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Write | FileShare.Delete))
        {
            error = await Record.ExceptionAsync(() =>
                ApplyAsync(fixture.Manager, operation, CancellationToken.None));
        }

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(ValidMetadata, await File.ReadAllTextAsync(fixture.MetadataPath));
        Assert.Empty(Directory.EnumerateFiles(fixture.MetadataDirectory, "*.tmp"));
    }

    private static Task ApplyAsync(HostWebSessionManager manager, string operation, CancellationToken token)
        => operation switch
        {
            "record" => manager.RecordVerifiedAsync("site.example.test", "alpha", "new-agent", token),
            "remove-one" => manager.RemoveMetadataAsync("site.example.test", token),
            "clear-all" => manager.RemoveMetadataAsync(host: null, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static void AssertOtherHostUnchanged(JsonElement item)
    {
        Assert.Equal("other.example.test", item.GetProperty("Host").GetString());
        Assert.Equal("other-agent", item.GetProperty("UserAgent").GetString());
        Assert.Equal("other-source", Assert.Single(item.GetProperty("SourceIds").EnumerateArray()).GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T10:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
            item.GetProperty("LastVerifiedAt").GetDateTimeOffset());
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), $"AniMeido-host-session-write-{Guid.NewGuid():N}");
        public string MetadataDirectory => Path.Combine(Root, "Sessions");
        public string MetadataPath => Path.Combine(MetadataDirectory, "metadata.json");
        public HostWebSessionManager Manager { get; }

        public Fixture()
        {
            Manager = new HostWebSessionManager(Root);
        }

        public async Task SeedAsync(string content)
        {
            Directory.CreateDirectory(MetadataDirectory);
            await File.WriteAllTextAsync(MetadataPath, content);
        }

        public async Task<JsonDocument> ReadDocumentAsync()
            => JsonDocument.Parse(await File.ReadAllTextAsync(MetadataPath));

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
