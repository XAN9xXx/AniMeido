using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class PluginPackagePickerTests
{
    [Fact]
    public void AppUsesDesktopPickerThatSupportsElevatedProcesses()
    {
        // 只核对实际构建的 API 依赖，不打开文件选择窗口。
        using var assembly = File.OpenRead(AppDataRootsTests.BuildOutput("AniMeido.App"));
        using var image = new PEReader(assembly);
        var metadata = image.GetMetadataReader();
        var referencedTypes = metadata.TypeReferences
            .Select(handle => metadata.GetTypeReference(handle))
            .Select(type => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}")
            .ToArray();

        Assert.Contains("Microsoft.Windows.Storage.Pickers.FileOpenPicker", referencedTypes);
        Assert.DoesNotContain("Windows.Storage.Pickers.FileOpenPicker", referencedTypes);
    }
}
