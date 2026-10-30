using AniMeido.Plugin.Player.Sources;
using AniMeido.Plugin.Player.Sources.EasyBangumi;
using AniMeido.Plugin.Player.Sources.Managed;
using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.Rules;
using AniMeido.Plugin.Player.Sources.Subscriptions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class SourceLoaderTests
{
    [Fact]
    public void InvalidRuleIsReportedOnceAndDoesNotHideAValidSource()
    {
        using var paths = new MockAppDataPaths();
        using var http = new HttpClient();
        var logger = new LoadLogger();
        File.WriteAllText(Path.Combine(paths.RootDirectory, "bad.animeido-source.json"), "{");
        File.WriteAllText(Path.Combine(paths.RootDirectory, "valid.animeido-source.json"), """
            {"kind":"static","id":"valid","displayName":"Valid","episodes":[
                {"id":"ep1","title":"Episode 1","mediaUrl":"https://example.test/1.mp4"}]}
            """);

        var loaded = SourceRuleLoader.Load(http, paths.RootDirectory, logger).ToArray();

        Assert.Equal("valid", Assert.Single(loaded).Id);
        AssertWarning(logger, "bad.animeido-source.json");
    }

    [Fact]
    public void InvalidManagedManifestIsReportedAtItsLoadBoundary()
    {
        using var paths = new MockAppDataPaths();
        var logger = new LoadLogger();
        var directory = Path.Combine(paths.RootDirectory, "broken-managed");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "source.json"), "{");

        Assert.Empty(ManagedSourceLoader.Load(paths.RootDirectory, logger));

        AssertWarning(logger, "source.json");
    }

    [Fact]
    public void InvalidSubscriptionIsReportedButValidAndDisabledPackagesKeepTheirSemantics()
    {
        using var paths = new MockAppDataPaths();
        using var http = new HttpClient();
        var logger = new LoadLogger();
        var broken = CreatePackageDirectory(paths.RootDirectory, "broken");
        File.WriteAllText(Path.Combine(broken, "source-package.json"), "{");
        var disabled = CreatePackageDirectory(paths.RootDirectory, "disabled");
        File.WriteAllText(Path.Combine(disabled, "source-package.json"), "{");
        File.WriteAllText(Path.Combine(disabled, SourcePackageDiscovery.DisabledMarkerName), string.Empty);
        var valid = CreatePackageDirectory(paths.RootDirectory, "valid");
        var manifest = new SourcePackageManifest
        {
            FormatVersion = 2,
            Id = "valid",
            DisplayName = "Valid",
            Version = "1.0.0",
            EntryFile = "source.js",
            SourceKind = "easybangumi-js",
            SubscriptionId = "fixture",
            UpstreamPath = "source.js",
            UpstreamRevision = "fixture-revision",
        };
        File.WriteAllText(Path.Combine(valid, "source-package.json"), JsonSerializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(valid, "source.js"), """
            function SearchComponent_search() { return new Pair(null, new ArrayList()); }
            function DetailedComponent_getDetailed() { return new Pair(null, new ArrayList()); }
            function PlayComponent_getPlayInfo() { return new PlayerInfo(0, "https://example.test"); }
            """);

        var loaded = SubscriptionSourceLoader.Load(http, null!,
            new EasyPreferenceStore(paths.RootDirectory), paths.RootDirectory, logger).ToArray();

        Assert.Equal("valid", Assert.Single(loaded).Id);
        AssertWarning(logger, "broken");
    }

    [Theory]
    [InlineData("rules")]
    [InlineData("managed")]
    public void FailedDiscoveryReportsItsFallbackToAnEmptyList(string kind)
    {
        using var paths = new MockAppDataPaths();
        using var http = new HttpClient();
        var logger = new LoadLogger();
        var file = Path.Combine(paths.RootDirectory, "not-a-directory");
        File.WriteAllText(file, "fixture");

        var loaded = Load(kind, file, http, logger).ToArray();

        Assert.Empty(loaded);
        AssertWarning(logger, "not-a-directory");
    }

    [Theory]
    [InlineData("rules")]
    [InlineData("managed")]
    [InlineData("subscriptions")]
    public void EmptySourceDirectoryDoesNotLogAnError(string kind)
    {
        using var paths = new MockAppDataPaths();
        using var http = new HttpClient();
        var logger = new LoadLogger();

        Assert.Empty(Load(kind, paths.RootDirectory, http, logger));
        Assert.Empty(logger.Entries);
    }

    private static IEnumerable<IOnlineAnimeSource> Load(
        string kind, string directory, HttpClient http, ILogger logger)
        => kind switch
        {
            "rules" => SourceRuleLoader.Load(http, directory, logger),
            "managed" => ManagedSourceLoader.Load(directory, logger),
            "subscriptions" => SubscriptionSourceLoader.Load(http, null!,
                new EasyPreferenceStore(directory), directory, logger),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string CreatePackageDirectory(string root, string name)
    {
        var path = Path.Combine(root, "Packages", name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AssertWarning(LoadLogger logger, string location)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.NotNull(entry.Exception);
        Assert.Contains(location, entry.Message);
    }

    private sealed class LoadLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
