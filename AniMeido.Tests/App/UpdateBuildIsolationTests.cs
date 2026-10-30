using AniMeido.App.Services;
using AniMeido.Updates;

namespace AniMeido.Tests;

public sealed class UpdateBuildIsolationTests
{
#if ANIMEIDO_UPDATE_TEST_SOURCE
    [Fact(Skip = "本地验收构建；生产隔离由 AniMeidoUpdateTestSource=false 构建验证。")]
#else
    [Fact]
#endif
    public void DefaultBuildHasNoTestTrustOrLocalMappingType()
    {
        Assert.Single(UpdateTrustedKeys.PublicKeys);
        Assert.Equal("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOJeoMG4coWpH3j9W3928GGN7ARuQ0VrYv9YvlD6QmghBy9I6YN4AdAAym/9lmfIaonp5VSaGuyE/9rNUEIB10A==",
            UpdateTrustedKeys.PublicKeys[0]);
        Assert.Null(typeof(InstalledUpdateService).Assembly.GetType("AniMeido.App.Services.UpdateTestSourceHandler"));
    }
}
