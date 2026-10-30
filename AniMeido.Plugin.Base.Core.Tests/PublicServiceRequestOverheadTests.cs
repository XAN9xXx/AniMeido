using System.Diagnostics;
using System.Net;
using AniMeido.Plugin.Base.Services;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class PublicServiceRequestOverheadTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task SuccessfulInMemoryResponsesRecordTheAddedCoordinatorCost()
    {
        using var handler = new MemoryHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test") };
        var requests = new PublicServiceRequests(http);
        var direct = await MeasureAsync(async () =>
        {
            using var response = await http.GetAsync("/", HttpCompletionOption.ResponseHeadersRead);
            return (await response.Content.ReadAsByteArrayAsync()).Length;
        });
        var coordinated = await MeasureAsync(() => requests.ReadAsync("/", async (response, ct) =>
            (await response.Content.ReadAsByteArrayAsync(ct)).Length, default));
        output.WriteLine("In-memory HTTP only; 20 warmups and 400 measured requests per path.");
        output.WriteLine("Direct: p50={0:F2} us, p95={1:F2} us, allocated={2} B/request", direct.P50, direct.P95, direct.Bytes);
        output.WriteLine("Coordinated: p50={0:F2} us, p95={1:F2} us, allocated={2} B/request", coordinated.P50, coordinated.P95, coordinated.Bytes);
        Assert.Equal(840, handler.Calls);
    }

    private static async Task<(double P50, double P95, long Bytes)> MeasureAsync(Func<Task<int>> read)
    {
        for (var i = 0; i < 20; i++)
            Assert.Equal(3, await read());
        var samples = new double[400];
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < samples.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Assert.Equal(3, await read());
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        var bytes = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / samples.Length;
        Array.Sort(samples);
        return (samples[200], samples[380], bytes);
    }

    private sealed class MemoryHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }
}
