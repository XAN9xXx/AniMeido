using AniMeido.Plugin.Base.Models;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

internal sealed class RecommendationSnapshotStore(CacheService cache) : IDisposable
{
    // 快照含作品 ID，不复用旧 Bangumi ID 空间中的快照。
    private const string CacheKey = "recommendations:metadata:snapshot:v1";
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(24);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly SemaphoreSlim _writeGate = new(1);
    private long _revision;

    public long Revision => Interlocked.Read(ref _revision);
    public long CaptureCacheGeneration() => cache.CaptureGeneration();

    internal static bool IsFresh(RecommendationSnapshot snapshot, DateTimeOffset now)
        => snapshot.SchemaVersion == RecommendationSnapshot.CurrentSchemaVersion
            && snapshot.GeneratedAt >= now - Freshness;

    public async Task<RecommendationSnapshot?> ReadAsync(bool allowExpired, CancellationToken cancellationToken)
    {
        var snapshot = await ReadPayloadAsync<RecommendationSnapshot>(cancellationToken);
        if (snapshot?.SchemaVersion != RecommendationSnapshot.CurrentSchemaVersion)
            return null;

        return allowExpired || IsFresh(snapshot, DateTimeOffset.UtcNow) ? snapshot : null;
    }

    public async Task<bool> MatchesRoundAsync(DateTimeOffset generatedAt, CancellationToken cancellationToken)
    {
        // 继续浏览只核对轮次，不重新构造已经展示过的作品和推荐理由。
        var identity = await ReadPayloadAsync<SnapshotIdentity>(cancellationToken);
        return identity?.SchemaVersion == RecommendationSnapshot.CurrentSchemaVersion
            && identity.GeneratedAt == generatedAt;
    }

    private async Task<T?> ReadPayloadAsync<T>(CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await cache.GetCacheAllowExpiredAsync(CacheKey);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            await cache.RemoveCacheIfMatchesAsync(CacheKey, json);
            return null;
        }
    }

    public async Task InvalidateAsync()
    {
        // 立即拒绝在途的旧结果，只有缓存 I/O 等待写入锁。
        Interlocked.Increment(ref _revision);
        await _writeGate.WaitAsync();
        try
        {
            await cache.RemoveCacheAsync(CacheKey);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> TryWriteAsync(RecommendationSnapshot snapshot, long cacheGeneration,
        long revision, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (revision != Revision)
                return false;

            await cache.SetCacheAsync(CacheKey, json, Retention, cacheGeneration);
            return revision == Revision;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // 推荐服务先排空完整业务操作，再释放这个存储。
    public void Dispose() => _writeGate.Dispose();

    private sealed record SnapshotIdentity(int SchemaVersion, DateTimeOffset GeneratedAt);
}
