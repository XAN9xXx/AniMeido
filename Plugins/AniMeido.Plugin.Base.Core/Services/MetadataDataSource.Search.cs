using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using System.Net;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed partial class MetadataDataSource
{
    internal async Task<KeywordSearchPage> SearchPageAsync(string keyword, int offset, string? format, bool complete,
        bool includeLowVotes, SearchContext? expected, CancellationToken ct, bool forceRefresh = false)
    {
        var budget = new ServiceReadBudget();
        var capability = await CachedAsync("metadata:search-capabilities", TimeSpan.FromMinutes(5), true,
            b => SendJsonAsync<MetadataCapabilities>("/v1/version", ct, b), ct, budget).ConfigureAwait(false);
        var rule = capability.Data?.Search;
        if (complete && rule?.CompleteWorkSort != true)
            throw MetadataServiceException.SearchFailure("full_sort_unavailable", "当前搜索暂时支持按相关性浏览，完整排序需要更新搜索服务。");
        if (format is not null && rule is null)
            throw MetadataServiceException.SearchFailure("full_sort_unavailable", "当前搜索暂时支持按相关性浏览，作品形式筛选需要更新搜索服务。");
        var escaped = Uri.EscapeDataString(keyword);
        var route = complete ? "/v1/search/brief" : "/v1/search";
        var path = $"{route}?q={escaped}&type=work" + (format is null ? "" : "&format=" + Uri.EscapeDataString(format));
        if (!complete) path += $"&offset={offset}&limit={PageSize}";
        if (expected is not null && rule is not null)
            path += "&expectedDataVersion=" + Uri.EscapeDataString(expected.DataVersion)
                + "&expectedSearchVersion=" + Uri.EscapeDataString(expected.SearchVersion)
                + "&expectedAsOf=" + Uri.EscapeDataString(expected.AsOf);
        var baseKey = $"metadata:keyword-v2:{escaped}:{format}:{complete}:{offset}";
        var key = $"{baseKey}:data:{_dataVersion}:search:{rule?.SearchVersion ?? "sql-1"}:day:{MetadataCalendar.Date(_time)}";
        var cached = await ReadCacheAsync<MetadataSearchPage>(key).ConfigureAwait(false);
        if (!forceRefresh && cached is not null && (expected is null || cached.Context == expected))
            return await PresentSearchPageAsync(cached, includeLowVotes, ct, budget).ConfigureAwait(false);
        MetadataSearchPage page;
        var generation = _cacheService.CaptureGeneration();
        try
        {
            page = await _requests.ReadAsync(path, async (response, token) =>
            {
                var version = await ObserveVersionAsync(response, generation).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
                {
                    var problem = JsonSerializer.Deserialize<MetadataProblem>(json, JsonOptions);
                    throw MetadataServiceException.SearchFailure(problem?.Code ?? "search_failed",
                        response.StatusCode == HttpStatusCode.Conflict ? "搜索结果已更新，请重新搜索。" : "结果较多，请缩小关键词或先选择作品形式后再排序。");
                }
                response.EnsureSuccessStatusCode();
                var result = JsonSerializer.Deserialize<MetadataSearchPage>(json, JsonOptions) ?? throw new JsonException("搜索结果为空。");
                result.DataVersion = version;
                result.FetchedAt = _time.GetUtcNow();
                if (result.SearchVersion.Length == 0) result.SearchVersion = "sql-1";
                if (result.AsOf.Length == 0) result.AsOf = MetadataCalendar.Date(_time);
                if (expected is not null && result.Context != expected)
                    throw MetadataServiceException.SearchFailure("search_context_changed", "搜索结果已更新，请重新搜索。");
                if (complete && (!result.Complete || result.Total != result.Items.Count || result.Items.Select(i => i.Id).Distinct().Count() != result.Total))
                    throw new MetadataServiceException("搜索结果未完整返回，请重试。");
                if (!complete && result.Items.Count == 0 && offset < result.Total)
                    throw new MetadataServiceException("搜索结果缺少下一页，请重试。");
                return result;
            }, ct, budget).ConfigureAwait(false);
        }
        catch (Exception error) when (!forceRefresh && !ct.IsCancellationRequested && IsServiceFailure(error)
            && error is not MetadataServiceException { Code: "search_context_changed" or "full_sort_unavailable" })
        {
            var last = await _cacheService.GetCacheAllowExpiredAsync(baseKey + ":last").ConfigureAwait(false);
            cached = last is null ? null : await ReadCacheAsync<MetadataSearchPage>(last, expired: true).ConfigureAwait(false);
            if (cached is null || expected is not null && cached.Context != expected)
            {
                if (error is MetadataServiceException) throw;
                throw new MetadataServiceException("搜索请求失败。", error);
            }
            cached.IsOffline = true;
            return await PresentSearchPageAsync(cached, includeLowVotes, ct, budget).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        var savedKey = $"{baseKey}:data:{page.DataVersion}:search:{page.SearchVersion}:day:{page.AsOf}";
        await _cacheService.SetCacheAsync(savedKey, JsonSerializer.Serialize(page, JsonOptions), TimeSpan.FromMinutes(5), generation).ConfigureAwait(false);
        await _cacheService.SetCacheAsync(baseKey + ":last", savedKey, TimeSpan.FromDays(30), generation).ConfigureAwait(false);
        return await PresentSearchPageAsync(page, includeLowVotes, ct, budget).ConfigureAwait(false);
    }

    internal async Task<KeywordSearchPage> PresentSearchPageAsync(MetadataSearchPage page, bool includeLowVotes,
        CancellationToken ct, ServiceReadBudget? budget = null)
    {
        if (!page.Complete)
        {
            var works = await WorksByIdsAsync(page.Items.Select(i => i.Id), ct, budget, includeLowVotes, page.DataVersion).ConfigureAwait(false);
            return new(page, works);
        }
        var today = Today;
        var items = page.Items.Select(item =>
        {
            var (year, month) = SeasonDate(item.FirstDate);
            return new Anime(item.Id, EntityName(item.MainNames), null, [], StartDate(item.FirstDate),
                ImageUrl("work", item.Id, "cover", false), "", year, month,
                Score: CompositeRating.ScoreForRanking(item.Ratings, item.FirstDate, today, includeLowVotes),
                MediaFormat: MapMediaFormat(item.Format));
        }).ToList();
        ct.ThrowIfCancellationRequested();
        return new(page, items);
    }
}
