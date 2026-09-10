using AniMeido.Plugin.Player.Sources.EasyBangumi;

namespace AniMeido.Tests;

public sealed class EasyScriptCompatibilityTests
{
    [Fact]
    public void Validate_AcceptsSupportedPlaybackSurface()
    {
        const string Script = """
            var helper = Inject_OkhttpHelper;
            function SearchComponent_search() { return new Pair(null, new ArrayList()); }
            function DetailedComponent_getDetailed() { return new Pair(null, new ArrayList()); }
            function PlayComponent_getPlayInfo() { return new PlayerInfo(0, "https://example.test"); }
            """;

        EasyScriptCompatibility.Validate(Script);
    }

    [Fact]
    public void Validate_RejectsUnknownHostApi()
    {
        const string Script = """
            var helper = Inject_UnknownHelper;
            function SearchComponent_search() { return new Pair(null, new ArrayList()); }
            function DetailedComponent_getDetailed() { return new Pair(null, new ArrayList()); }
            function PlayComponent_getPlayInfo() { return new PlayerInfo(0, "https://example.test"); }
            """;

        var exception = Assert.Throws<InvalidDataException>(
            () => EasyScriptCompatibility.Validate(Script));

        Assert.Contains("Inject_UnknownHelper", exception.Message);
    }

    [Fact]
    public void Prelude_ForwardsActionScriptAndLegacyParser()
    {
        Assert.Contains("strategy.actionJs", EasyBangumiPrelude.Script);
        Assert.Contains("strategy.useLegacyParser", EasyBangumiPrelude.Script);
    }

    [Fact]
    public void ResolvedHeaders_PreserveAuthoritativeWebResolverIdentity()
    {
        var headers = EasyBangumiSource.MergeResolvedHeaders(
            new Dictionary<string, string>
            {
                ["Referer"] = "https://source.test/",
            },
            new Dictionary<string, string>
            {
                ["Referer"] = "https://player.test/embed/",
                ["Origin"] = "https://player.test",
                ["Cookie"] = "session=temporary",
                ["User-Agent"] = "browser",
            });

        Assert.Equal(4, headers.Count);
        Assert.Equal(
            "https://player.test/embed/",
            headers["Referer"]);
        Assert.Equal("https://player.test", headers["Origin"]);
        Assert.Equal("session=temporary", headers["Cookie"]);
        Assert.Equal("browser", headers["User-Agent"]);
    }
}
