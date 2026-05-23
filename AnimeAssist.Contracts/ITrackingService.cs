using AniMeido.Contracts.Models;

namespace AniMeido.Contracts
{
    public interface ITrackingService
    {
        Task SetStatusAsync(int animeId, AnimeTrackingStatus status);
        Task<AnimeTrackingStatus?> GetStatusAsync(int animeId);
        Task<List<int>> GetAnimeIdsByStatusAsync(AnimeTrackingStatus status);
        Task RemoveStatusAsync(int animeId);
    }
}