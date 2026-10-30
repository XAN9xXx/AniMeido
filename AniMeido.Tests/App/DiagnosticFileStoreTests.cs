using System.Reflection;

using System.Text.RegularExpressions;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class DiagnosticFileStoreTests
{
    [Fact]
    public void OnlyFileCreationCanRetryAsANameCollision()
    {
        var source = File.ReadAllText(Path.Combine(AppDataRootsTests.RepositoryRoot,
            "Shared", "DiagnosticFileStore.cs"));
        var start = source.IndexOf("private static void WriteError(", StringComparison.Ordinal);
        var end = source.IndexOf("private void CleanupOldDays(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = source[start..end];
        var collision = Regex.Match(body,
            @"try\s*\{(?<create>.*?)\}\s*catch\s*\(IOException\)\s*when\s*\(File\.Exists\(path\)\)\s*\{(?<retry>.*?)\}",
            RegexOptions.Singleline);
        Assert.True(collision.Success);
        Assert.Contains("new FileStream(", collision.Groups["create"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("stream.Write(", collision.Groups["create"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("using", collision.Groups["create"].Value, StringComparison.Ordinal);
        Assert.Contains("continue;", collision.Groups["retry"].Value, StringComparison.Ordinal);
        var write = body[(collision.Index + collision.Length)..];
        Assert.Matches(@"using\s*\(\s*stream\s*\)", write);
        Assert.Contains("stream.Write(bytes)", write, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AniMeido.App", "warnings")]
    [InlineData("AniMeido.PluginHost", "plugin-host-fixture-warnings")]
    public void BothBuildOutputsKeepRotationErrorCollisionAndRetentionRules(string assemblyName, string warningStem)
    {
        using var paths = new MockAppDataPaths();
        var root = Path.Combine(paths.RootDirectory, "logs");
        var now = DateTimeOffset.Now;
        var old = Path.Combine(root, "warning", now.AddDays(-3).ToString("yyyy-MM-dd"));
        var retained = Path.Combine(root, "warning", now.AddDays(-2).ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(retained);
        File.WriteAllText(Path.Combine(old, "old.log"), "old");
        File.WriteAllText(Path.Combine(retained, "keep.log"), "keep");
        var assembly = Assembly.LoadFrom(AppDataRootsTests.BuildOutput(assemblyName));
        var type = assembly.GetType("AniMeido.Logging.DiagnosticFileStore");
        Assert.NotNull(type);
        var store = Activator.CreateInstance(type, [warningStem]);
        var write = type.GetMethod("Write", BindingFlags.Instance | BindingFlags.Public)!;
        void Write(bool warning, string kind, string text)
            => write.Invoke(store, [root, warning, now, kind, text]);

        for (var index = 0; index < 20; index++)
            Write(true, "Warning", $"Warning {index}: {new string('x', 4096)}\n");
        Write(true, "Warning", new string('y', 64 * 1024));
        Write(false, "InvalidOperationException", "first failure");
        Write(false, "InvalidOperationException", "second failure");

        var today = now.ToString("yyyy-MM-dd");
        var warnings = Directory.GetFiles(Path.Combine(root, "warning", today));
        Assert.True(warnings.Length > 1);
        Assert.All(warnings, path =>
        {
            Assert.StartsWith(warningStem, Path.GetFileName(path));
            Assert.InRange(new FileInfo(path).Length, 1, 50 * 1024);
        });
        var warningText = string.Join("\n", warnings.Select(File.ReadAllText));
        Assert.Contains("Warning 19", warningText);
        Assert.Contains("[Warning truncated]", warningText);
        var errors = Directory.GetFiles(Path.Combine(root, "error", today));
        Assert.Equal(2, errors.Length);
        Assert.All(errors, path => Assert.Contains("InvalidOperationException", Path.GetFileName(path)));
        var errorText = string.Join("\n", errors.Select(File.ReadAllText));
        Assert.Contains("first failure", errorText);
        Assert.Contains("second failure", errorText);
        Assert.False(Directory.Exists(old));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(retained, "keep.log")));
    }
}
