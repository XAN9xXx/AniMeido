using AniMeido.Contracts;
using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.Services;

/// <summary>负数 ID 是本地保留记录，只从迁移登记读标题，不交给远端公共资料服务。</summary>
internal sealed class PersonalAnimeDataSource(MetadataDataSource remote, SqliteConnectionFactory db) : IAnimeDataSource
{
    public async Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
    {
        if (animeID >= 0) return await remote.GetAnimeDetailAsync(animeID, ct).ConfigureAwait(false);
        using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT TitleSnapshot FROM legacy_anime_refs WHERE LocalId=@id";
        cmd.Parameters.AddWithValue("@id", animeID);
        var title = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return new Anime(animeID, title ?? $"Bangumi 条目 #{-(long)animeID}", null, [], null, null, "资料不可用", 0, 0);
    }
    public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => remote.GetAnimeBySeasonAsync(year, season, ct);
    public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => remote.GetCurrentBroadcastScheduleAsync(ct);
    public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => remote.GetStudioAsync(animeID, ct);
    public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => remote.GetTagsAsync(animeID, ct);
    public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => remote.GetCVsAsync(animeID, ct);
    public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => remote.GetCharacterRolesAsync(animeID, ct);
    public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => remote.GetPersonWorksAsync(personId, ct);
    public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => remote.SearchByKeywordAsync(keyword, offset, ct);
    public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
        => remote.SearchByTagAsync(tag, offset, sort, ct, airDateFrom, airDateTo);
}
