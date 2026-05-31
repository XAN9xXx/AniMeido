using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Plugin.Base.ViewModels
{
    public partial class AnimeDetailViewModel : ObservableObject
    {
        [ObservableProperty]
        private Anime? _animeDetail = null;
        [ObservableProperty]
        private bool _isLoading = false;
        [ObservableProperty]
        string? _errorMessage = null;
        [ObservableProperty]
        private bool _isError = false;
        [ObservableProperty]
        private bool _hasData = false;
        [ObservableProperty]
        private AnimeTrackingStatus _currentStatus = AnimeTrackingStatus.None;
        [ObservableProperty]
        private bool _isCurrentSeason = false;
        [ObservableProperty]
        private bool _isOldSeason = false;
        public string? BangumiUrl => _lastAnimeID > 0
            ? $"https://bgm.tv/subject/{_lastAnimeID}"
            : null;
        private int _lastAnimeID;
        private readonly IAnimeDataSource _animeDataSource;
        private readonly TrackingService _trackingService;



        public AnimeDetailViewModel(IAnimeDataSource dataSource, TrackingService trackingService)
        {
            _animeDataSource = dataSource;
            _trackingService = trackingService;
        }



        /// <summary>
        /// 加载番剧详情页面数据
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        [RelayCommand]
        private async Task LoadDetailAsync(int animeID)
        {
            IsLoading = true;
            IsError = false;
            ErrorMessage = null;
            HasData = false;
            AnimeDetail = null;
            _lastAnimeID = animeID;
            try
            {
                AnimeDetail = await _animeDataSource.GetAnimeDetailAsync(animeID, CancellationToken.None);
                HasData = true;
                OnPropertyChanged(nameof(BangumiUrl));

                // 判断是当前季还是往季
                IsCurrentSeason = false;
                IsOldSeason = false;
                if (AnimeDetail?.SeasonMonth > 0 && AnimeDetail.SeasonYear > 0)
                {
                    var currentSeason = SeasonHelper.GetCurrentSeason();
                    var animeSeason = SeasonHelper.FromMonth(AnimeDetail.SeasonMonth);
                    IsCurrentSeason = AnimeDetail.SeasonYear == currentSeason.year && animeSeason == currentSeason.season;
                    IsOldSeason = !IsCurrentSeason;
                }

                // 加载详情后查询当前关注状态
                var status = await _trackingService.GetStatusAsync(animeID);
                CurrentStatus = status ?? AnimeTrackingStatus.None;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Fail to load: {ex.Message}";
                IsError = true;
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private void RetryLoad()
        {
            LoadDetailCommand.Execute(_lastAnimeID);
        }


        [RelayCommand]
        private async Task SetWatchingAsync()
        {
            if (_lastAnimeID <= 0) return;
            if (CurrentStatus == AnimeTrackingStatus.Watching)
            {
                await _trackingService.RemoveStatusAsync(_lastAnimeID);
                CurrentStatus = AnimeTrackingStatus.None;
            }
            else
            {
                await _trackingService.SetStatusAsync(_lastAnimeID, AnimeTrackingStatus.Watching);
                CurrentStatus = AnimeTrackingStatus.Watching;
            }
        }

        [RelayCommand]
        private async Task SetPlanToWatchAsync()
        {
            if (_lastAnimeID <= 0) return;
            if (CurrentStatus == AnimeTrackingStatus.PlanToWatch)
            {
                await _trackingService.RemoveStatusAsync(_lastAnimeID);
                CurrentStatus = AnimeTrackingStatus.None;
            }
            else
            {
                await _trackingService.SetStatusAsync(_lastAnimeID, AnimeTrackingStatus.PlanToWatch);
                CurrentStatus = AnimeTrackingStatus.PlanToWatch;
            }
        }

        [RelayCommand]
        private async Task SetNotInterestedAsync()
        {
            if (_lastAnimeID <= 0) return;
            if (CurrentStatus == AnimeTrackingStatus.NotInterested)
            {
                await _trackingService.RemoveStatusAsync(_lastAnimeID);
                CurrentStatus = AnimeTrackingStatus.None;
            }
            else
            {
                await _trackingService.SetStatusAsync(_lastAnimeID, AnimeTrackingStatus.NotInterested);
                CurrentStatus = AnimeTrackingStatus.NotInterested;
            }
        }

        [RelayCommand]
        private async Task SetFollowingAsync()
        {
            if (_lastAnimeID <= 0) return;
            if (CurrentStatus == AnimeTrackingStatus.Following)
            {
                await _trackingService.RemoveStatusAsync(_lastAnimeID);
                CurrentStatus = AnimeTrackingStatus.None;
            }
            else
            {
                await _trackingService.SetStatusAsync(_lastAnimeID, AnimeTrackingStatus.Following);
                CurrentStatus = AnimeTrackingStatus.Following;
            }
        }

        [RelayCommand]
        private async Task SetCompletedAsync()
        {
            if (_lastAnimeID <= 0) return;
            if (CurrentStatus == AnimeTrackingStatus.Completed)
            {
                await _trackingService.RemoveStatusAsync(_lastAnimeID);
                CurrentStatus = AnimeTrackingStatus.None;
            }
            else
            {
                await _trackingService.SetStatusAsync(_lastAnimeID, AnimeTrackingStatus.Completed);
                CurrentStatus = AnimeTrackingStatus.Completed;
            }
        }

        [RelayCommand]
        private async Task SetDroppedAsync()
        {
            if (_lastAnimeID <= 0) return;
            if (CurrentStatus == AnimeTrackingStatus.Dropped)
            {
                await _trackingService.RemoveStatusAsync(_lastAnimeID);
                CurrentStatus = AnimeTrackingStatus.None;
            }
            else
            {
                await _trackingService.SetStatusAsync(_lastAnimeID, AnimeTrackingStatus.Dropped);
                CurrentStatus = AnimeTrackingStatus.Dropped;
            }
        }

        [RelayCommand]
        private async Task SetBlockedAsync()
        {
            if (_lastAnimeID <= 0) return;
            if (CurrentStatus == AnimeTrackingStatus.Blocked)
            {
                await _trackingService.RemoveStatusAsync(_lastAnimeID);
                CurrentStatus = AnimeTrackingStatus.None;
            }
            else
            {
                await _trackingService.SetStatusAsync(_lastAnimeID, AnimeTrackingStatus.Blocked);
                CurrentStatus = AnimeTrackingStatus.Blocked;
            }
        }

        [RelayCommand]
        private async Task ClearTrackingStatusAsync()
        {
            if (_lastAnimeID <= 0) return;
            await _trackingService.RemoveStatusAsync(_lastAnimeID);
            CurrentStatus = AnimeTrackingStatus.None;
        }
    }
}
