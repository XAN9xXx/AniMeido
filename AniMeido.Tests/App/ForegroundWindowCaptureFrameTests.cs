using System.Text.RegularExpressions;

namespace AniMeido.Tests;

public sealed class ForegroundWindowCaptureFrameTests
{
    [Fact]
    [Trait("Category", "Mechanism")]
    public void EmptyFrameReturnsBeforePublishingOrDisposingTheFrame()
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "AniMeido.App",
            "Services", "ForegroundWindowCaptureService.cs");
        var source = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*|/\*[\s\S]*?\*/", string.Empty);
        var header = Regex.Match(source, @"\bvoid\s+OnFrameArrived\s*\([^)]*\)\s*\{");
        Assert.True(header.Success, "The check must inspect the production frame-arrived handler.");
        var start = header.Index + header.Length;
        var end = start;
        var depth = 1;
        while (end < source.Length && depth > 0)
        {
            if (source[end] == '{')
                depth++;
            else if (source[end] == '}')
                depth--;
            end++;
        }
        Assert.Equal(0, depth);
        var body = source[start..(end - 1)];

        var read = Regex.Match(body,
            @"\b(?:var|[\w.]+\??)\s+(?<frame>\w+)\s*=\s*\w+\s*\.\s*TryGetNextFrame\s*\(\s*\)\s*;");
        Assert.True(read.Success, "Capture the local frame returned by the real frame pool.");
        var frame = Regex.Escape(read.Groups["frame"].Value);
        var guard = Regex.Match(body, @"\bif\s*\(\s*(?:" + frame + @"\s+is\s+null|"
            + frame + @"\s*==\s*null|null\s*==\s*" + frame
            + @")\s*\)\s*(?:\{\s*return\s*;\s*\}|return\s*;)");
        Assert.True(guard.Success, "A legal empty frame must leave the handler without completing the capture.");
        var publish = Regex.Match(body, @"\b\w+\s*\.\s*TrySetResult\s*\(\s*" + frame + @"\s*\)");
        var dispose = Regex.Match(body, @"\b" + frame + @"\s*\??\.\s*Dispose\s*\(\s*\)");
        Assert.True(publish.Success, "Nonempty frames must still enter the existing completion path.");
        Assert.True(dispose.Success, "Unclaimed nonempty frames must still have their existing disposal path.");
        Assert.True(guard.Index >= read.Index + read.Length
            && guard.Index + guard.Length <= Math.Min(publish.Index, dispose.Index),
            "The empty-frame return must precede both result publication and frame disposal.");
    }
}
