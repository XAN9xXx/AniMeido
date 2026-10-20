using AniMeido.Contracts;
using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.Services;

/// <summary>负数 ID 是本地保留记录，只从迁移登记读标题，不交给远端公共资料服务。</summary>
internal sealed class PersonalAnimeDataSource(MetadataDataSource remote, SqliteConnectionFactory db) : IAnimeDataSource
{
    public async Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
    {
        if (animeID == 0) return null;
        using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand(); cmd.CommandText = animeID < 0
            ? "SELECT TitleSnapshot FROM legacy_anime_refs WHERE LocalId=@id"
            : "SELECT TitleSnapshot FROM public_id_issues WHERE AnimeId=@id AND Kind='deactivated'";
        cmd.Parameters.AddWithValue("@id", animeID);
        var snapshot = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (animeID > 0 && snapshot is null)
        {
            var anime = await remote.GetAnimeDetailAsync(animeID, ct).ConfigureAwait(false);
            if (anime is not null && anime.ID != animeID)
            {
                // 资料可以跟随合并目标，但个人记录身份只能由本地改写改变。
                // 缓存里也可能已经是合并目标，因此不能只在收到 404 时排队。
                remote.QueueMergedCheck(animeID);
                return anime with { ID = animeID };
            }
            return anime;
        }
        var title = snapshot as string;
        return new Anime(animeID, title ?? (animeID < 0 ? $"Bangumi 条目 #{-(long)animeID}" : $"作品 #{animeID}"), null, [], null, null, "资料不可用", 0, 0);
    }
    internal static bool IsUnavailable(Anime anime) => anime.ID < 0 || (anime.Description == "资料不可用" && anime.SeasonYear == 0);

    private async Task<bool> IsUnavailableAsync(int id, CancellationToken ct)
    {
        if (id <= 0) return true;
        using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM public_id_issues WHERE AnimeId=@id AND Kind='deactivated'";
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }
    public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => remote.GetAnimeBySeasonAsync(year, season, ct);
    public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => remote.GetCurrentBroadcastScheduleAsync(ct);
    public async Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => await IsUnavailableAsync(animeID, ct).ConfigureAwait(false) ? [] : await remote.GetStudioAsync(animeID, ct).ConfigureAwait(false);
    public async Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => await IsUnavailableAsync(animeID, ct).ConfigureAwait(false) ? [] : await remote.GetTagsAsync(animeID, ct).ConfigureAwait(false);
    public async Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => await IsUnavailableAsync(animeID, ct).ConfigureAwait(false) ? [] : await remote.GetCVsAsync(animeID, ct).ConfigureAwait(false);
    public async Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => await IsUnavailableAsync(animeID, ct).ConfigureAwait(false) ? [] : await remote.GetCharacterRolesAsync(animeID, ct).ConfigureAwait(false);
    public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => remote.GetPersonWorksAsync(personId, ct);
    public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => remote.SearchByKeywordAsync(keyword, offset, ct);
    public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
        => remote.SearchByTagAsync(tag, offset, sort, ct, airDateFrom, airDateTo);
}
