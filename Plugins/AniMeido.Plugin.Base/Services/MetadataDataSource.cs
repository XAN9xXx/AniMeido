using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed partial class MetadataDataSource : IAnimeDataSource
{
    private const int PageSize = 20;
    private readonly HttpClient _httpClient;
    private readonly ILogger<MetadataDataSource> _logger;
    private readonly CacheService _cacheService;
    private readonly TimeProvider _time;

    internal MetadataDataSource(ILogger<MetadataDataSource> logger, HttpClient httpClient,
        CacheService cacheService, TimeProvider? time = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _time = time ?? TimeProvider.System;
    }

    private async Task<MetadataPage<T>> FetchPagesAsync<T>(string path, CancellationToken ct)
    {
        var all = new MetadataPage<T>();
        var offset = 0;
        while (true)
        {
            var separator = path.Contains('?') ? '&' : '?';
            var page = await SendJsonAsync<MetadataPage<T>>($"{path}{separator}offset={offset}&limit=100", ct)
                .ConfigureAwait(false);
            if (offset == 0) all.DataVersion = page.DataVersion;
            else if (all.DataVersion != page.DataVersion)
                throw new MetadataServiceException("分页期间资料版本变化，请重试。");
            if (page.Items.Count == 0 && offset < page.Total)
                throw new MetadataServiceException("资料服务在完整读取分页前返回了空页。");
            all.Items.AddRange(page.Items);
            all.Total = page.Total;
            offset += page.Items.Count;
            if (offset >= page.Total) return all;
        }
    }

    public async Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
    {
        if (!Enum.IsDefined(season)) throw new ArgumentOutOfRangeException(nameof(season));
        var result = await CachedAsync($"metadata:season:{year}:{season}", TimeSpan.FromHours(12), true,
            () => FetchPagesAsync<MetadataWork>($"/v1/works?season={year:D4}-{SeasonHelper.ToMonth(season):D2}", ct), ct)
            .ConfigureAwait(false);
        return result.Items.Where(w => w.Id > 0).DistinctBy(w => w.Id).Select(w => MapAnime(w)).ToList();
    }

    public async Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
    {
        var date = DateOnly.FromDateTime(_time.GetLocalNow().DateTime).ToString("yyyy-MM-dd");
        var result = await CachedAsync($"metadata:airing:{date}", TimeSpan.FromHours(12), true,
            () => SendJsonAsync<MetadataPage<MetadataWork>>($"/v1/works/airing?date={date}", ct), ct)
            .ConfigureAwait(false);
        return result.Items.Where(w => w.Id > 0).DistinctBy(w => w.Id).Select(w => MapAnime(w)).ToList();
    }

    private async Task<MetadataDetail> FetchDetailAsync(int id, CancellationToken ct, bool followMerge = true)
    {
        var result = await SendJsonAsync<MetadataDetail>($"/v1/works/{id}", ct,
            static (json, version) => new MetadataDetail
            {
                DataVersion = version,
                Problem = JsonSerializer.Deserialize<MetadataProblem>(json, JsonOptions)
                    ?? throw new JsonException("资料服务返回空的错误响应。"),
            }).ConfigureAwait(false);
        if (result.Problem is { Code: "merged", MergedInto: > 0 } problem && followMerge && problem.MergedInto != id)
        {
            QueueMergedCheck(id);
            return await FetchDetailAsync(problem.MergedInto.Value, ct, followMerge: false).ConfigureAwait(false);
        }
        if (result.Problem is { Code: not ("merged" or "not_found" or "deactivated") })
            throw new MetadataServiceException("资料服务返回未知的 404 错误。");
        return result;
    }

    private async Task<MetadataWork?> DetailAsync(int id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id <= 0) return null;
        var detail = await CachedAsync($"metadata:detail:{id}", TimeSpan.FromDays(7), false,
            () => FetchDetailAsync(id, ct), ct).ConfigureAwait(false);
        return detail.Data;
    }

    public async Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) =>
        await DetailAsync(animeID, ct).ConfigureAwait(false) is { } work ? MapAnime(work, detail: true) : null;

    public async Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) =>
        await DetailAsync(animeID, ct).ConfigureAwait(false) is { } work ? Studios(work) : [];

    public async Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) =>
        await DetailAsync(animeID, ct).ConfigureAwait(false) is { } work ? VoiceActors(work) : [];

    public async Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
    {
        var work = await DetailAsync(animeID, ct).ConfigureAwait(false);
        return work is null ? [] : Characters(work).Where(c => c.Character.Id > 0)
            .Select(c => new CharacterRole(c.Character.Id, EntityName(c.Character.MainNames), null,
                ImageUrl("character", c.Character.Id, "character", true),
                c.Cv.Where(v => v.Id > 0).DistinctBy(v => v.Id).Select(VoiceActor).ToList())).ToList();
    }

    private Task<MetadataPage<MetadataVocab>> VocabAsync(string category, CancellationToken ct) =>
        CachedAsync($"metadata:vocab:{category}", TimeSpan.FromDays(7), true,
            () => SendJsonAsync<MetadataPage<MetadataVocab>>($"/v1/vocab?category={category}", ct), ct);

    private async Task<List<MetadataVocab>> TagVocabAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pages = new List<MetadataPage<MetadataVocab>>();
            foreach (var category in new[] { "tag", "format", "country", "source_type" })
                pages.Add(await VocabAsync(category, ct).ConfigureAwait(false));
            if (pages.Select(p => p.DataVersion).Distinct().Count() == 1)
                return pages.SelectMany(p => p.Items).ToList();
            _logger.LogWarning("Metadata version changed while reading tag vocabularies; retrying");
        }
        throw new MetadataServiceException("读取标签词表期间资料版本变化，请重试。");
    }

    public async Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
    {
        var work = await DetailAsync(animeID, ct).ConfigureAwait(false);
        if (work is null) return [];
        var vocab = await TagVocabAsync(ct).ConfigureAwait(false);
        return TagCodes(work).Select(code => vocab.FirstOrDefault(v => v.Category == code.Category && v.Code == code.Code))
            .Where(v => v is not null).Select(v => new Tag(v!.NameZh)).DistinctBy(t => t.Name).ToList();
    }

    private async Task<List<Anime>> WorksByIdsAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var requested = ids.Where(id => id > 0).Distinct().ToList();
        if (requested.Count == 0) return [];
        var items = new Dictionary<int, Anime>();
        foreach (var batch in requested.Chunk(100))
        {
            var query = string.Join(',', batch);
            var response = await CachedAsync($"metadata:works:{query}", TimeSpan.FromHours(12), true,
                () => SendJsonAsync<MetadataPage<MetadataWork>>($"/v1/works?ids={query}", ct), ct)
                .ConfigureAwait(false);
            foreach (var work in response.Items.Where(w => w.Id > 0 && w.Status is not ("merged" or "deactivated" or "not_found")))
                items[work.Id] = MapAnime(work);
        }
        // 批量响应按服务顺序返回，必须还原搜索/本地排序选出的页内顺序。
        return requested.Where(items.ContainsKey).Select(id => items[id]).ToList();
    }

    public async Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (string.IsNullOrWhiteSpace(keyword)) return ([], 0);
        var query = Uri.EscapeDataString(keyword);
        var response = await CachedAsync($"metadata:search:{query}:{offset}", TimeSpan.FromMinutes(5), true,
            () => SendJsonAsync<MetadataPage<MetadataSearchItem>>($"/v1/search?q={query}&type=work&offset={offset}&limit={PageSize}", ct), ct)
            .ConfigureAwait(false);
        return (await WorksByIdsAsync(response.Items.Select(i => i.Id), ct).ConfigureAwait(false), response.Total);
    }

    public async Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
        CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var vocab = await TagVocabAsync(ct).ConfigureAwait(false);
        var entry = vocab.FirstOrDefault(v => v.NameZh == tag);
        if (entry is null)
        {
            _logger.LogWarning("Unknown metadata tag name: {Tag}", tag);
            return ([], 0);
        }
        var filter = $"{TagParameter(entry.Category)}={Uri.EscapeDataString(entry.Code)}";
        var response = await CachedAsync($"metadata:brief:{filter}", TimeSpan.FromHours(12), true,
            () => SendJsonAsync<MetadataPage<MetadataBrief>>($"/v1/brief?{filter}", ct), ct).ConfigureAwait(false);
        var from = FullDate(airDateFrom);
        var to = FullDate(airDateTo);
        IEnumerable<MetadataBrief> items = response.Items.Where(i => i.Id > 0).DistinctBy(i => i.Id);
        // 只有年或年月的首播日期按该年、该月的 1 日比较，否则新番和待播作品会被整体滤掉。
        if (from.HasValue) items = items.Where(i => StartDate(i.FirstDate) >= from.Value);
        if (to.HasValue) items = items.Where(i => StartDate(i.FirstDate) < to.Value);
        items = sort switch
        {
            "rank" => items.OrderByDescending(i => BangumiRating(i.Ratings)?.Score.HasValue == true)
                .ThenByDescending(i => BangumiRating(i.Ratings)?.Score)
                .ThenByDescending(i => BangumiRating(i.Ratings)?.Votes).ThenBy(i => i.Id),
            "date" => items.OrderByDescending(i => i.FirstDate, StringComparer.Ordinal).ThenBy(i => i.Id),
            _ => items,
        };
        var sorted = items.ToList();
        return (await WorksByIdsAsync(sorted.Skip(offset).Take(PageSize).Select(i => i.Id), ct).ConfigureAwait(false), sorted.Count);
    }

    public async Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (personId <= 0) return [];
        var response = await CachedAsync($"metadata:person-works:{personId}", TimeSpan.FromDays(7), false,
            () => FetchPagesAsync<MetadataPersonWork>($"/v1/participants/{personId}/works", ct), ct).ConfigureAwait(false);
        var roles = response.Items.Any(i => i.Role is not null)
            ? (await VocabAsync("staff_role", ct).ConfigureAwait(false)).Items : [];
        return response.Items.Where(i => i.Work.Id > 0).GroupBy(i => i.Work.Id).Select(group =>
        {
            var work = group.First().Work;
            var staff = group.Select(i => i.Role is null && i.Character is not null ? "配音"
                : roles.FirstOrDefault(v => v.Code == i.Role)?.NameZh ?? i.Role).Where(s => s is not null).Distinct();
            return new PersonWork(work.Id, EntityName(work.MainNames), string.Join("、", staff),
                ImageUrl("work", work.Id, "cover", false), BangumiRating(work.Ratings)?.Score, FullDate(work.FirstDate));
        }).ToList();
    }
}
