using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;

namespace AniMeido.Plugin.Base.Services;

/// <summary>负数 ID 是本地保留记录，只从迁移登记读标题，不交给远端公共资料服务。</summary>
internal sealed class PersonalAnimeDataSource(
    MetadataDataSource remote,
    SqliteConnectionFactory db) : IAnimeDataSource, IMetadataCacheScope
{
    MetadataCacheStamp IMetadataCacheScope.CacheStamp => ((IMetadataCacheScope)remote).CacheStamp;

    public async Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
    {
        if (animeID == 0)
        {
            return null;
        }

        var snapshot = await ReadTitleSnapshotAsync(animeID, ct).ConfigureAwait(false);
        if (animeID > 0 && !snapshot.Exists)
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

        return new UnavailableAnime(animeID, snapshot.Title ?? AnimeTitleFallback.Untitled);
    }

    private Task<(bool Exists, string? Title)> ReadTitleSnapshotAsync(int animeId, CancellationToken ct)
        => Task.Run(async () =>
        {
            using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = animeId < 0
                ? "SELECT TitleSnapshot FROM legacy_anime_refs WHERE LocalId=@id"
                : "SELECT TitleSnapshot FROM public_id_issues WHERE AnimeId=@id AND Kind='deactivated'";
            command.Parameters.AddWithValue("@id", animeId);
            var title = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return (title is not null, title as string);
        }, ct);

    internal static bool IsUnavailable(Anime anime) => anime.ID < 0 || anime is UnavailableAnime;

    private Task<bool> IsUnavailableAsync(int id, CancellationToken ct)
    {
        if (id <= 0)
        {
            return Task.FromResult(true);
        }

        return Task.Run(async () =>
        {
            using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM public_id_issues WHERE AnimeId=@id AND Kind='deactivated'";
            command.Parameters.AddWithValue("@id", id);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        }, ct);
    }

    public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        => remote.GetAnimeBySeasonAsync(year, season, ct);

    public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
        => remote.GetCurrentBroadcastScheduleAsync(ct);

    public async Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
    {
        if (await IsUnavailableAsync(animeID, ct).ConfigureAwait(false))
        {
            return [];
        }

        return await remote.GetStudioAsync(animeID, ct).ConfigureAwait(false);
    }

    public async Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
    {
        if (await IsUnavailableAsync(animeID, ct).ConfigureAwait(false))
        {
            return [];
        }

        return await remote.GetTagsAsync(animeID, ct).ConfigureAwait(false);
    }

    public async Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
    {
        if (await IsUnavailableAsync(animeID, ct).ConfigureAwait(false))
        {
            return [];
        }

        return await remote.GetCVsAsync(animeID, ct).ConfigureAwait(false);
    }

    public async Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
    {
        if (await IsUnavailableAsync(animeID, ct).ConfigureAwait(false))
        {
            return [];
        }

        return await remote.GetCharacterRolesAsync(animeID, ct).ConfigureAwait(false);
    }

    public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
        => remote.GetPersonWorksAsync(personId, ct);

    public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(
        string keyword,
        int offset,
        CancellationToken ct)
        => remote.SearchByKeywordAsync(keyword, offset, ct);

    public Task<(List<Anime> Results, int Total)> SearchByTagAsync(
        string tag,
        int offset,
        string sort,
        CancellationToken ct,
        string? airDateFrom = null,
        string? airDateTo = null)
        => remote.SearchByTagAsync(tag, offset, sort, ct, airDateFrom, airDateTo);
}
