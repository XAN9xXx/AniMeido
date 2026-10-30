using AniMeido.Plugin.Player.Sources.EasyBangumi;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class EasyHtmlPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [Trait("Category", "Performance")]
    public void RepeatedFieldExtractionKeepsEveryResult(int count)
    {
        var html = "<html><body>" + string.Concat(Enumerable.Range(0, count).Select(index =>
            $"<div class='row'><a href='/episode/{index}'><span class='name'>Episode {index}</span></a><img src='/cover/{index}.png'></div>"))
            + "</body></html>";
        for (var warmup = 0; warmup < 20; warmup++)
        {
            Extract(html, count);
        }
        var durations = new List<double>();
        var allocations = new List<long>();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            Extract(html, count);
            durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - bytes);
        }
        output.WriteLine($"Rows={count}; MedianMs={durations.Order().ElementAt(2):F3}; MedianBytes={allocations.Order().ElementAt(2)}");
    }

    private static void Extract(string html, int count)
    {
        var document = new EasyHtmlDocument(html, "https://example.test/");
        var rows = document.select(".row");
        var xpath = new EasyXPathFacade();
        Assert.Equal(count, rows.size());
        for (var index = 0; index < rows.size(); index++)
        {
            var row = rows.get(index)!;
            Assert.Equal($"/episode/{index}", row.select("a").first()!.attr("href"));
            Assert.Equal($"Episode {index}", row.select(".name").text());
            Assert.Equal($"/cover/{index}.png", row.select("img").first()!.attr("src"));
            Assert.Equal($"Episode {index}", xpath.text(row, "//span"));
            Assert.Equal($"/episode/{index}", xpath.attr(row, "//a", "href"));
            Assert.Equal($"/cover/{index}.png", xpath.attr(row, "//img", "src"));
        }
    }
}
