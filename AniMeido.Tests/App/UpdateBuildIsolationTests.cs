using AniMeido.App.Services;
using AniMeido.Updates;

namespace AniMeido.Tests;

public sealed class UpdateBuildIsolationTests
{
    [Fact]
    public void DefaultBuildHasNoTestTrustOrLocalMappingType()
    {
        Assert.Empty(UpdateTrustedKeys.PublicKeys);
        Assert.Null(typeof(InstalledUpdateService).Assembly.GetType("AniMeido.App.Services.UpdateTestSourceHandler"));
    }
}
