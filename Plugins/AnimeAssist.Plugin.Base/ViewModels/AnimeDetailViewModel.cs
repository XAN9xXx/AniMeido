using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using System.Collections.ObjectModel;
using System.Text.Json;

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
        [ObservableProperty]
        private string? _studiosText = null;

        public ObservableCollection<CharacterRole> Characters { get; } = new();

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
        private async Task LoadDetailAsync(int animeID, CancellationToken ct = default)
        {
            IsLoading = true;
            IsError = false;
            ErrorMessage = null;
            HasData = false;
            AnimeDetail = null;
            _lastAnimeID = animeID;
            try
            {
                AnimeDetail = await _animeDataSource.GetAnimeDetailAsync(animeID, ct);
                if (AnimeDetail is null)
                {
                    HasData = false;
                    IsError = true;
                    ErrorMessage = "未找到该番剧或数据暂不可用。";
                    return;
                }

                HasData = true;
                OnPropertyChanged(nameof(BangumiUrl));

                // 并行加载 Studio 和角色
                await Task.WhenAll(
                    LoadStudiosAsync(animeID),
                    LoadCharactersAsync(animeID)
                );

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
            catch (HttpRequestException ex)
            {
                ErrorMessage = $"网络请求失败：{ex.Message}";
                IsError = true;
            }
            catch (TaskCanceledException)
            {
                // 取消是预期行为（用户导航离开/快速切换），不当作错误处理
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                ErrorMessage = $"数据解析失败：{ex.Message}";
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
            await SetTrackingStatusAsync(AnimeTrackingStatus.Watching);
        }

        [RelayCommand]
        private async Task SetPlanToWatchAsync()
        {
            await SetTrackingStatusAsync(AnimeTrackingStatus.PlanToWatch);
        }

        [RelayCommand]
        private async Task SetNotInterestedAsync()
        {
            await SetTrackingStatusAsync(AnimeTrackingStatus.NotInterested);
        }

        [RelayCommand]
        private async Task SetFollowingAsync()
        {
            await SetTrackingStatusAsync(AnimeTrackingStatus.Following);
        }

        [RelayCommand]
        private async Task SetCompletedAsync()
        {
            await SetTrackingStatusAsync(AnimeTrackingStatus.Completed);
        }

        [RelayCommand]
        private async Task SetDroppedAsync()
        {
            await SetTrackingStatusAsync(AnimeTrackingStatus.Dropped);
        }

        [RelayCommand]
        private async Task SetBlockedAsync()
        {
            await SetTrackingStatusAsync(AnimeTrackingStatus.Blocked);
        }

        /// <summary>统一设置番剧状态：已设置则取消，未设置则设置。</summary>
        private async Task SetTrackingStatusAsync(AnimeTrackingStatus status)
        {
            if (_lastAnimeID <= 0) return;
            if (CurrentStatus == status)
            {
                await _trackingService.RemoveStatusAsync(_lastAnimeID);
                CurrentStatus = AnimeTrackingStatus.None;
            }
            else
            {
                await _trackingService.SetStatusAsync(_lastAnimeID, status);
                CurrentStatus = status;
            }
        }

        [RelayCommand]
        private async Task ClearTrackingStatusAsync()
        {
            if (_lastAnimeID <= 0) return;
            await _trackingService.RemoveStatusAsync(_lastAnimeID);
            CurrentStatus = AnimeTrackingStatus.None;
        }

        private async Task LoadStudiosAsync(int animeID)
        {
            try
            {
                var studios = await _animeDataSource.GetStudioAsync(animeID, CancellationToken.None);
                StudiosText = studios.Count > 0
                    ? $"制作/原作：{string.Join("、", studios.Select(s => s.Name))}"
                    : null;
            }
            catch (HttpRequestException)
            {
                // Studio 网络请求失败不阻塞详情
            }
            catch (JsonException)
            {
                // Studio 解析失败不阻塞详情
            }
        }

        private async Task LoadCharactersAsync(int animeID)
        {
            try
            {
                var characters = await _animeDataSource.GetCharacterRolesAsync(animeID, CancellationToken.None);
                Characters.Clear();
                foreach (var c in characters)
                    Characters.Add(c);
            }
            catch (HttpRequestException)
            {
                // 角色网络请求失败不阻塞详情
            }
            catch (JsonException)
            {
                // 角色解析失败不阻塞详情
            }
        }
    }
}
