using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

internal static class RecommendationTestServices
{
    internal static RecommendationService CreateService(SqliteConnectionFactory factory,
        RecommendationCandidateProvider candidates, CacheService? cache = null) => new(
        factory, new TrackingService(factory), new SavedTagService(factory),
        new ArchiveService(factory), new BrowseHistoryService(factory),
        new ActionCenterService(factory), cache ?? new CacheService(factory), candidates);

    internal static RecommendationCandidateProvider CreateCandidates()
        => new(new NoNetworkSource(), NullLogger<RecommendationCandidateProvider>.Instance);

    private sealed class NoNetworkSource : IAnimeDataSource
    {
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => throw new NotSupportedException("Personal data operations must not request candidates.");

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag,
            int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null)
            => throw new NotSupportedException();

        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(
            string keyword, int offset, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
