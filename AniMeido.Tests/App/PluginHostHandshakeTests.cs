using AniMeido.PluginProtocol;
using System.Reflection;
using System.Text.Json;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class PluginHostHandshakeTests
{
    private const string CallbackPipeName = "AniMeido.Handshake.Callback";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task MatchingProtocolAndCallbackReturnTheActualHostVersion()
    {
        var (hostAssembly, dispatch) = CreateTarget();
        var request = CreateHandshakeRequest(
            PluginHostProtocol.Version,
            CallbackPipeName);

        var response = Assert.IsType<PluginHostHandshakeResponse>(
            await dispatch(request).WaitAsync(Budget));

        Assert.Equal(PluginHostProtocol.Version, response.ProtocolVersion);
        var hostVersion = hostAssembly.GetName().Version;
        Assert.NotNull(hostVersion);
        Assert.Equal(hostVersion.ToString(3), response.HostVersion);
    }

    [Theory]
    [InlineData(PluginHostProtocol.Version - 1)]
    [InlineData(PluginHostProtocol.Version + 1)]
    public async Task DifferentProtocolVersionIsRejected(int protocolVersion)
    {
        var (_, dispatch) = CreateTarget();
        var request = CreateHandshakeRequest(protocolVersion, CallbackPipeName);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatch(request).WaitAsync(Budget));

        Assert.Equal(
            $"IPC 协议版本不兼容：App={protocolVersion}, Host={PluginHostProtocol.Version}。",
            failure.Message);
    }

    [Theory]
    [InlineData("AniMeido.Handshake.Other")]
    [InlineData("animeido.handshake.callback")]
    public async Task CallbackPipeRequiresAnExactOrdinalMatch(string callbackPipeName)
    {
        var (_, dispatch) = CreateTarget();
        var request = CreateHandshakeRequest(
            PluginHostProtocol.Version,
            callbackPipeName);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatch(request).WaitAsync(Budget));

        Assert.Equal("回调管道身份不匹配。", failure.Message);
    }

    private static JsonPipeRpcRequest CreateHandshakeRequest(
        int protocolVersion,
        string callbackPipeName)
        => new(
            1,
            "HandshakeAsync",
            [
                JsonSerializer.SerializeToElement(new PluginHostHandshakeRequest(
                    protocolVersion,
                    "1.0.0",
                    "fixture-instance",
                    callbackPipeName)),
            ]);

    private static (
        Assembly HostAssembly,
        Func<JsonPipeRpcRequest, Task<object?>> Dispatch) CreateTarget()
    {
        var assembly = Assembly.LoadFrom(
            AppDataRootsTests.BuildOutput("AniMeido.PluginHost"));
        var type = assembly.GetType(
            "AniMeido.PluginHost.PluginHostRpcTarget",
            throwOnError: true)!;

        // 握手不读取插件目录；不为这个入口构造 WinUI Catalog。
        var target = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [null, CallbackPipeName],
            culture: null);
        Assert.NotNull(target);
        var dispatch = type.GetMethod(
            "DispatchAsync",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(dispatch);

        return (
            assembly,
            dispatch.CreateDelegate<Func<JsonPipeRpcRequest, Task<object?>>>(target));
    }
}
