using AniMeido.Plugin.Base.Services;
using AniMeido.Updates.Install;

namespace AniMeido.Tests;

public sealed class BaseCoreBoundaryTests
{
    [Fact]
    public void BuildOutputs_ContainCoreNativeSqliteAndOriginalSchemaMetadata()
    {
        var app = AppDataRootsTests.BuildOutput("AniMeido.App");
        var output = Path.GetDirectoryName(app)!;
        Assert.True(File.Exists(Path.Combine(output, "AniMeido.Plugin.Base.Core.dll")));
        Assert.True(File.Exists(Path.Combine(output, "e_sqlite3.dll")));
        Assert.Equal(PersonalDatabaseSchema.CurrentVersion, InstalledDatabaseVersion.Read(app));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("versions/2.1.2")]
    public void OldReader_ReadsRealBaseDllWithoutLoadingCore(string layout)
    {
        using var paths = new MockAppDataPaths();
        var directory = Path.Combine(paths.RootDirectory, layout);
        Directory.CreateDirectory(directory);
        File.Copy(AppDataRootsTests.BuildOutput("AniMeido.Plugin.Base"),
            Path.Combine(directory, "AniMeido.Plugin.Base.dll"));

        Assert.Equal(PersonalDatabaseSchema.CurrentVersion,
            InstalledDatabaseVersion.Read(Path.Combine(directory, "AniMeido.App.exe")));
    }

    [Fact]
    public void Core_HasNoWindowsUiReferenceAndPreservesResourceAndProductVersion()
    {
        var core = typeof(PersonalDatabaseSchema).Assembly;
        var desktop = typeof(DatabaseSchema).Assembly;
        Assert.NotEqual(desktop, core);
        Assert.Equal(desktop.GetName().Version, core.GetName().Version);
        Assert.DoesNotContain(core.GetReferencedAssemblies(),
            reference => reference.Name!.StartsWith("Microsoft.UI", StringComparison.Ordinal)
                || reference.Name.StartsWith("Microsoft.Windows", StringComparison.Ordinal));
        using var resource = core.GetManifestResourceStream("AniMeido.BangumiMetaTags");
        Assert.NotNull(resource);
        Assert.True(resource.Length > 0);
    }
}
