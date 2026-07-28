using AniMeido.Plugin.Player;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class PlayerPluginIdentityTests
{
    [Fact]
    public void Identity_MatchesRuntimeAssemblyMetadata()
    {
        var assembly = typeof(PlayerPlugin).Assembly;
        var expectedId = assembly.GetName().Name;
        var expectedName = assembly
            .GetCustomAttribute<AssemblyTitleAttribute>()?
            .Title;
        var expectedVersion = assembly.GetName().Version?.ToString(3);
        var plugin = new PlayerPlugin();

        Assert.NotNull(expectedId);
        Assert.NotNull(expectedName);
        Assert.NotNull(expectedVersion);
        Assert.Equal(expectedId, plugin.PluginID);
        Assert.Equal(expectedName, plugin.DisplayName);
        Assert.Equal(expectedVersion, plugin.Version);
    }
}
