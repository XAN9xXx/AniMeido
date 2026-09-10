using AniMeido.PluginProtocol;

namespace AniMeido.PluginProtocol.Tests;

public sealed class ProtocolV5Tests
{
    [Fact]
    public void ProtocolV5_HandshakeIncludesDedicatedCallbackPipe()
    {
        var request = new PluginHostHandshakeRequest(
            PluginHostProtocol.Version,
            "1.7.0",
            "test-instance",
            "AniMeido-callback-test");

        Assert.Equal(5, PluginHostProtocol.Version);
        Assert.Equal("AniMeido-callback-test", request.CallbackPipeName);
    }
}
