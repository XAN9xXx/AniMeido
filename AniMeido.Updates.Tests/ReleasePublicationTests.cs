using System.Security.Cryptography;
using AniMeido.UpdateSigner;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class ReleasePublicationTests : IDisposable
{
    private const string Version = "3.0.0";
    private readonly TemporaryDirectory _temp = new();
    private readonly ECDsa _key = TestData.Key();
    private readonly byte[] _package = [1, 2, 3];

    private void Prepare(string version = Version, string channel = "stable", string? url = null)
    {
        var document = TestData.Document(_package);
        document.Version = version;
        document.Channel = channel;
        var zip = $"AniMeido-{version}-win-x64.zip";
        document.Package!.Urls = [url ?? $"https://dl.animeido.com/updates/win-x64/{channel}/{zip}"];
        var bytes = TestData.Bytes(document);
        File.WriteAllBytes(_temp.PathFor("release.json"), bytes);
        File.WriteAllBytes(_temp.PathFor("release.json.sig"), UpdateSignature.Sign(bytes, _key));
        File.WriteAllBytes(_temp.PathFor(zip), _package);
        File.WriteAllBytes(_temp.PathFor($"AniMeido_setup_{version}.exe"), [1]);
    }

    private bool Check(string version = Version, string? current = null) =>
        ReleasePublication.ShouldPublish(_temp.Root, version, current, [TestData.Public(_key)]);

    [Fact]
    public void FirstReleasePublishesAndIdenticalRerunRepairsDiscoveryOnly()
    {
        Prepare();
        Assert.True(Check());
        Assert.False(Check(current: _temp.PathFor("release.json")));
    }

    [Theory]
    [InlineData("3.0.0-preview.2", "3.0.0-preview.10", true)]
    [InlineData("3.0.0-preview.10", "3.0.0-preview.2", false)]
    [InlineData("3.0.1", Version, false)]
    [InlineData("3.0.0+one", "3.0.0+two", false)]
    public void OrdersWithinChannel(string oldVersion, string newVersion, bool advances)
    {
        var channel = oldVersion.Contains('-') ? "preview" : "stable";
        Prepare(oldVersion, channel);
        var current = _temp.PathFor("current.json");
        File.Copy(_temp.PathFor("release.json"), current);
        Prepare(newVersion, channel);
        if (advances) Assert.True(Check(newVersion, current));
        else Assert.Throws<UpdateException>(() => Check(newVersion, current));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("zip")]
    [InlineData("size")]
    [InlineData("url")]
    [InlineData("version")]
    [InlineData("channel")]
    [InlineData("installer")]
    public void RejectsInconsistentAttachments(string fault)
    {
        Prepare(url: fault == "url" ? "https://example.com/wrong.zip" : null,
            channel: fault == "channel" ? "preview" : "stable");
        if (fault == "signature") File.AppendAllText(_temp.PathFor("release.json"), " ");
        if (fault == "zip") File.WriteAllBytes(_temp.PathFor("AniMeido-3.0.0-win-x64.zip"), [1, 2, 4]);
        if (fault == "size") File.WriteAllBytes(_temp.PathFor("AniMeido-3.0.0-win-x64.zip"), [1]);
        if (fault == "installer") File.WriteAllBytes(_temp.PathFor("AniMeido_setup_3.0.0.exe"), []);
        Assert.Throws<UpdateException>(() => Check(fault == "version" ? "3.0.1" : Version));
    }

    public void Dispose()
    {
        _key.Dispose();
        _temp.Dispose();
    }
}
