using AniMeido.App.Services;
using AniMeido.Plugin.Base;

namespace AniMeido.Tests;

public sealed class BasePluginIdentityTests
{
    [Fact]
    public void Version_MatchesRuntimeAssemblyVersion()
    {
        var expectedVersion = typeof(BasePlugin).Assembly.GetName().Version?.ToString(3);

        Assert.NotNull(expectedVersion);
        Assert.Equal(expectedVersion, new BasePlugin().Version);
    }

    [Fact]
    public void Version_MatchesAppProductVersion()
    {
        // App 与 Base 共用 Directory.Build.props 中的 AniMeidoVersion，二者必须一致。
        var appVersion = typeof(UpdateService).Assembly.GetName().Version?.ToString(3);

        Assert.NotNull(appVersion);
        Assert.Equal(appVersion, new BasePlugin().Version);
    }
}
