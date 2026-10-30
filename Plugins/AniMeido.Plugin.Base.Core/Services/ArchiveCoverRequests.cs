using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Base.Services;

// 取用和失效由页面串行调用；多个呈现位置只等待同一个读取任务。
internal sealed class ArchiveCoverRequests(
    Func<int, CancellationToken, Task<Anime?>> fetch,
    ILogger logger)
{
    private readonly Dictionary<int, Task<Anime?>> _requests = [];
    private readonly SemaphoreSlim _fetchLimit = new(4);
    private readonly ILogger _logger = logger;

    internal Task<Anime?> GetOrStart(int animeId, CancellationToken cancellationToken)
    {
        if (!_requests.TryGetValue(animeId, out var request)
            || request.IsFaulted || request.IsCanceled)
        {
            request = FetchAsync(animeId, cancellationToken);
            _requests[animeId] = request;
        }
        return request;
    }

    internal bool IsCurrent(int animeId, Task<Anime?>? request)
        => _requests.TryGetValue(animeId, out var current)
            && ReferenceEquals(current, request);

    internal void Clear() => _requests.Clear();

    internal void DiscardIncomplete()
    {
        foreach (var animeId in _requests
                     .Where(pair => !pair.Value.IsCompletedSuccessfully)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _requests.Remove(animeId);
        }
    }

    private async Task<Anime?> FetchAsync(int animeId, CancellationToken cancellationToken)
    {
        await _fetchLimit.WaitAsync(cancellationToken);
        try
        {
            return await fetch(animeId, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or MetadataServiceException
            or IOException or InvalidOperationException or TaskCanceledException or JsonException)
        {
            if (!cancellationToken.IsCancellationRequested)
                _logger.LogWarning(ex, "Archive cover details unavailable for {AnimeId}; keeping the cover empty", animeId);
            throw;
        }
        finally
        {
            _fetchLimit.Release();
        }
    }
}
