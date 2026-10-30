using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class CorePlatformAnalysisConfigurationTests
{
    [Theory]
    [InlineData("Shared/AppDataRoots.cs", "Plugins/AniMeido.Plugin.Base.Core/AniMeido.Plugin.Base.Core.csproj")]
    [InlineData("AniMeido.Tests/PausedUiContext.cs", "AniMeido.Plugin.Base.Core.Tests/AniMeido.Plugin.Base.Core.Tests.csproj")]
    [InlineData("AniMeido.Tests/MockAppDataPaths.cs", "AniMeido.Plugin.Base.Core.Tests/AniMeido.Plugin.Base.Core.Tests.csproj")]
    public void LinkedCoreSourceHasAnExplicitPhysicalPathPlatformAnalysisRule(string physicalPath, string projectPath)
    {
        var root = AppDataRootsTests.RepositoryRoot;
        var configuration = File.ReadAllText(Path.Combine(root, ".editorconfig"));
        var section = Assert.Single(Regex.Matches(configuration,
            @"^\[" + Regex.Escape(physicalPath) + @"\][ \t]*\r?\n(?<settings>.*?)(?=^\[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline));
        var defaultSection = configuration.IndexOf("[*.cs]", StringComparison.Ordinal);
        Assert.True(defaultSection >= 0 && section.Index > defaultSection);
        Assert.Matches(@"(?m)^[ \t]*dotnet_diagnostic\.CA1416\.severity[ \t]*=[ \t]*warning[ \t]*\r?$",
            section.Groups["settings"].Value);

        var projectFile = Path.Combine(root, projectPath);
        var project = XDocument.Load(projectFile);
        var expected = Path.GetFullPath(Path.Combine(root, physicalPath));
        Assert.Contains(project.Descendants("Compile"), item =>
        {
            var include = (string?)item.Attribute("Include");
            return include is not null && string.Equals(
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile)!, include)),
                expected,
                StringComparison.OrdinalIgnoreCase);
        });
    }
}
