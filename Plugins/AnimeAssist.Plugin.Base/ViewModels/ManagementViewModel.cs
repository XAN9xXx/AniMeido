using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Plugin.Base.ViewModels
{
    public partial class ManagementViewModel : ObservableObject
    {
        private readonly TrackingService _trackingService;
        private readonly IAnimeDataSource _animeDataSource;


        [ObservableProperty]
        private ObservableCollection<Anime> _watchingList = [];
        [ObservableProperty]
        private ObservableCollection<Anime> _planToWatchList = [];
        [ObservableProperty]
        private ObservableCollection<Anime> _notInterestedList = [];
        [ObservableProperty]
        private bool _isLoading = false;
        [ObservableProperty]
        private bool _isError = false;
        [ObservableProperty]
        string? _errorMessage = null;
        [ObservableProperty]
        private int _watchingCount = 0;
        [ObservableProperty]
        private int _planToWatchCount = 0;
        [ObservableProperty]
        private int _notInterestedCount = 0;
        [ObservableProperty]
        private int _selectedTabIndex = 0;


        public ManagementViewModel(TrackingService trackingService, IAnimeDataSource dataSource)
        {
            _trackingService = trackingService;
            _animeDataSource = dataSource;
        }


        [RelayCommand]
        private async Task LoadDataAsync()
        {
            IsLoading = true;
            IsError = false;
            ErrorMessage = null;

            try
            {
                // 获取各状态的番剧 ID 列表
                var watchingIds = await _trackingService.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Watching);
                var planIds = await _trackingService.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.PlanToWatch);
                var notInterestedIds = await _trackingService.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.NotInterested);

                // 逐个加载番剧详情（最多显示 20 条）
                WatchingList.Clear();
                foreach (var id in watchingIds.Take(20))
                {
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, CancellationToken.None);
                    if (anime != null) WatchingList.Add(anime);
                }

                PlanToWatchList.Clear();
                foreach (var id in planIds.Take(20))
                {
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, CancellationToken.None);
                    if (anime != null) PlanToWatchList.Add(anime);
                }

                NotInterestedList.Clear();
                foreach (var id in notInterestedIds.Take(20))
                {
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, CancellationToken.None);
                    if (anime != null) NotInterestedList.Add(anime);
                }

                WatchingCount = WatchingList.Count;
                PlanToWatchCount = PlanToWatchList.Count;
                NotInterestedCount = NotInterestedList.Count;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"加载失败：{ex.Message}";
                IsError = true;
            }
            finally
            {
                IsLoading = false;
            }
        }


        [RelayCommand]
        private async Task RemoveFromWatchingAsync(int animeId)
        {
            await _trackingService.RemoveStatusAsync(animeId);
            var item = WatchingList.FirstOrDefault(a => a.ID == animeId);
            if (item != null) WatchingList.Remove(item);
            WatchingCount = WatchingList.Count;
        }

        [RelayCommand]
        private async Task RemoveFromPlanAsync(int animeId)
        {
            await _trackingService.RemoveStatusAsync(animeId);
            var item = PlanToWatchList.FirstOrDefault(a => a.ID == animeId);
            if (item != null) PlanToWatchList.Remove(item);
            PlanToWatchCount = PlanToWatchList.Count;
        }

        [RelayCommand]
        private async Task RemoveFromNotInterestedAsync(int animeId)
        {
            await _trackingService.RemoveStatusAsync(animeId);
            var item = NotInterestedList.FirstOrDefault(a => a.ID == animeId);
            if (item != null) NotInterestedList.Remove(item);
            NotInterestedCount = NotInterestedList.Count;
        }
    }
}
