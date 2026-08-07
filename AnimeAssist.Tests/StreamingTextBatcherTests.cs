using AniMeido.Plugin.AI.Services;

namespace AniMeido.Tests;

public sealed class StreamingTextBatcherTests
{
    [Fact]
    public void Drain_ReturnsOnlyTextAppendedSincePreviousDrain()
    {
        var batcher = new StreamingTextBatcher();

        batcher.Append("第一段");
        Assert.Equal("第一段", batcher.Drain());
        Assert.Equal(string.Empty, batcher.Drain());

        batcher.Append("第二段");
        batcher.Append("第三段");

        Assert.Equal("第二段第三段", batcher.Drain());
        Assert.Equal(9, batcher.Length);
    }

    [Fact]
    public void Append_IgnoresEmptyChunks()
    {
        var batcher = new StreamingTextBatcher();

        batcher.Append(string.Empty);

        Assert.Equal(0, batcher.Length);
        Assert.Equal(string.Empty, batcher.Drain());
    }
}
