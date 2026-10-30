using AniMeido.Plugin.Player.Sources.Web;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class WebMediaResolverPatternTests(ITestOutputHelper output)
{
    private const string MediaPattern =
        """https?://[^\s"'<>\\]+?(?:\.m3u8|\.mp4)(?:\?[^\s"'<>\\]*)?""";
    private const string NamedMediaPattern = "(?<v>" + MediaPattern + ")";
    private static readonly Uri Page = new("https://source.example.test/watch/index.html");

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "Performance")]
    public void Matching_ReportsElapsedTimeAndAllocationsWithoutDroppingResults(int count)
    {
        var candidates = new string[count];
        var expectedMatches = new bool[count];
        var expectedUris = new Uri?[count];
        var expectedMatchCount = 0;
        for (var index = 0; index < count; index++)
        {
            var matches = index % 4 != 0;
            var extension = (index % 4) switch
            {
                0 => "js",
                2 => "m3u8",
                _ => "mp4",
            };
            candidates[index] = $"https://media.example.test/episode-{index:D5}.{extension}"
                + $"?part={index}&token=temporary";
            expectedMatches[index] = matches;
            expectedUris[index] = matches ? new Uri(candidates[index]) : null;
            if (matches)
            {
                expectedMatchCount++;
            }
        }

        for (var warmup = 0; warmup < 20; warmup++)
        {
            WebMediaUrlParser.TryMatchObservedMediaUrl(
                candidates[warmup], NamedMediaPattern, Page, out _);
        }

        var actualMatches = new bool[count];
        var actualUris = new Uri[count];
        var elapsed = new double[5];
        var allocations = new long[5];
        for (var iteration = 0; iteration < elapsed.Length; iteration++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var index = 0; index < candidates.Length; index++)
            {
                actualMatches[index] = WebMediaUrlParser.TryMatchObservedMediaUrl(
                    candidates[index], NamedMediaPattern, Page, out actualUris[index]);
            }
            elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocations[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            for (var index = 0; index < candidates.Length; index++)
            {
                Assert.Equal(expectedMatches[index], actualMatches[index]);
                Assert.Equal(expectedUris[index], actualUris[index]);
            }
        }

        output.WriteLine(
            "Candidates={0}; Matched={1}; VerifiedResultsPerRound={0}; Rounds={2}; MedianMs={3:F3}; MedianBytes={4}",
            count, expectedMatchCount, elapsed.Length,
            elapsed.Order().ElementAt(2), allocations.Order().ElementAt(2));
    }

    [Theory]
    [InlineData("https://media.example.test/episode.mp4", MediaPattern,
        "https://media.example.test/episode.mp4")]
    [InlineData("https://media.example.test/episode.m3u8?token=temporary", NamedMediaPattern,
        "https://media.example.test/episode.m3u8?token=temporary")]
    [InlineData("https://media.example.test/EPISODE.MP4", NamedMediaPattern,
        "https://media.example.test/EPISODE.MP4")]
    [InlineData("https://media.example.test/episode.mp4?part=1&amp;token=temporary", NamedMediaPattern,
        "https://media.example.test/episode.mp4?part=1&token=temporary")]
    [InlineData("/video/episode.mp4", "(?<v>/video/episode\\.mp4)",
        "https://source.example.test/video/episode.mp4")]
    [InlineData("https://media.example.test/script.js", NamedMediaPattern, null)]
    [InlineData("https://googleads.example.test/episode.mp4", NamedMediaPattern, null)]
    [InlineData("https://player.example.test/embed?url=https%3A%2F%2Fmedia.example.test%2Fepisode.m3u8",
        NamedMediaPattern, null)]
    [InlineData("https://media.example.test/episode.mp4", "", null)]
    [InlineData("https://media.example.test/episode.mp4", "   ", null)]
    public void Matching_PreservesObservedUrlSemantics(string candidate, string pattern, string? expected)
    {
        var matched = WebMediaUrlParser.TryMatchObservedMediaUrl(candidate, pattern, Page, out var mediaUri);

        Assert.Equal(expected is not null, matched);
        if (expected is not null)
        {
            Assert.Equal(expected, mediaUri.AbsoluteUri);
        }
    }

    [Fact]
    public void Matching_IsCaseInsensitiveRegardlessOfCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var matched = WebMediaUrlParser.TryMatchObservedMediaUrl(
                "https://media.example.test/file.mp4",
                "https://media\\.example\\.test/FILE\\.mp4",
                Page,
                out var mediaUri);

            Assert.True(matched);
            Assert.Equal("https://media.example.test/file.mp4", mediaUri.AbsoluteUri);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Matching_InvalidPatternStillThrowsRegexParseException()
    {
        Assert.Throws<RegexParseException>(() => WebMediaUrlParser.TryMatchObservedMediaUrl(
            "https://media.example.test/episode.mp4", "[", Page, out _));
    }
}
