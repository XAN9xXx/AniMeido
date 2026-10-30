using AniMeido.Plugin.Player.Sources.Web;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class HostWebSessionReadResultTests
{
    private const string ValidMetadata = """
        [
          {
            "Host": "older.example.test",
            "UserAgent": "older-agent",
            "SourceIds": ["older-source"],
            "LastVerifiedAt": "2026-10-19T10:00:00+00:00"
          },
          {
            "Host": "newer.example.test",
            "UserAgent": "newer-agent",
            "SourceIds": ["newer-source"],
            "LastVerifiedAt": "2026-10-20T10:00:00+00:00"
          }
        ]
        """;

    [Theory]
    [InlineData("{ broken session metadata")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"Host\":\"site.example.test\",\"SourceIds\":null}]")]
    [InlineData("[{\"Host\":null,\"SourceIds\":[]}]")]
    public async Task InvalidMetadataReturnsAnEmptyListAndTheCurrentFailure(string original)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(original);

        var result = await fixture.Manager.ListAsync(CancellationToken.None);

        Assert.Empty(result.Items);
        if (original.StartsWith('{'))
        {
            Assert.IsAssignableFrom<JsonException>(result.ReadFailure);
        }
        else
        {
            Assert.IsType<InvalidDataException>(result.ReadFailure);
        }
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.MetadataPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingMetadataIsAnEmptyListWithoutFailureOrFileCreation(bool sessionsDirectoryExists)
    {
        using var fixture = new Fixture();
        if (sessionsDirectoryExists)
        {
            Directory.CreateDirectory(fixture.MetadataDirectory);
        }

        var result = await fixture.Manager.ListAsync(CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Null(result.ReadFailure);
        Assert.False(File.Exists(fixture.MetadataPath));
        Assert.Equal(sessionsDirectoryExists, Directory.Exists(fixture.MetadataDirectory));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidMetadataPreservesOrderingAndReportsNoFailure(bool hasItems)
    {
        using var fixture = new Fixture();
        var original = hasItems ? ValidMetadata : "[]";
        await fixture.SeedAsync(original);

        var result = await fixture.Manager.ListAsync(CancellationToken.None);

        Assert.Null(result.ReadFailure);
        Assert.Equal(hasItems ? new[] { "newer.example.test", "older.example.test" } : [],
            result.Items.Select(item => item.Host));
        if (hasItems)
        {
            Assert.Equal("newer-agent", result.Items[0].UserAgent);
            Assert.Equal("newer-source", Assert.Single(result.Items[0].SourceIds));
        }
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.MetadataPath));
    }

    [Fact]
    public async Task RepairingTheFileClearsTheFailureOnTheNextReadOfTheSameManager()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("{ broken session metadata");
        var failed = await fixture.Manager.ListAsync(CancellationToken.None);
        Assert.IsAssignableFrom<JsonException>(failed.ReadFailure);
        Assert.Empty(failed.Items);

        await fixture.SeedAsync(ValidMetadata);
        var repaired = await fixture.Manager.ListAsync(CancellationToken.None);

        Assert.Null(repaired.ReadFailure);
        Assert.Equal(new[] { "newer.example.test", "older.example.test" },
            repaired.Items.Select(item => item.Host));
    }

    [Fact]
    public async Task UnreadableFileReportsTheIoFailureAndPreservesTheFile()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(ValidMetadata);
        HostWebSessionReadResult result;
        using (var unreadable = new FileStream(
            fixture.MetadataPath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Write | FileShare.Delete))
        {
            result = await fixture.Manager.ListAsync(CancellationToken.None);
        }

        Assert.Empty(result.Items);
        Assert.True(result.ReadFailure is IOException or UnauthorizedAccessException);
        Assert.Equal(ValidMetadata, await File.ReadAllTextAsync(fixture.MetadataPath));
    }

    [Fact]
    public async Task PreCancelledReadPropagatesCancellationWithoutChangingMetadata()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(ValidMetadata);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Manager.ListAsync(cancellation.Token));

        Assert.Equal(ValidMetadata, await File.ReadAllTextAsync(fixture.MetadataPath));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), $"AniMeido-host-session-read-{Guid.NewGuid():N}");
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

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
