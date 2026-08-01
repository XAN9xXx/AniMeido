using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
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
        [NotifyPropertyChangedFor(nameof(CurrentStatusHint))]
        [NotifyPropertyChangedFor(nameof(HasCurrentStatus))]
        private AnimeTrackingStatus _currentStatus = AnimeTrackingStatus.None;
        [ObservableProperty]
        private bool _isCurrentSeason = false;
        [ObservableProperty]
        private bool _isOldSeason = false;
        [ObservableProperty]
        private string _releasePhaseText = "日期未知";
        [ObservableProperty]
        private string _mediaFormatText = "其他动画";
        [ObservableProperty]
        private string? _studiosText = null;

        public ObservableCollection<CharacterRole> Characters { get; private set; } = new();

        public string? BangumiUrl => _lastAnimeID > 0
            ? $"https://bgm.tv/subject/{_lastAnimeID}"
            : null;
        private int _lastAnimeID;
        private readonly IAnimeDataSource _animeDataSource;
        private readonly TrackingService _trackingService;

        public ObservableCollection<TrackingActionDescriptor> TrackingActions
        {
            get;
        } = new(TrackingActionDescriptor.CreateDefaults());

        public IReadOnlyList<TrackingActionDescriptor> VisibleTrackingActions =>
            TrackingActions.Where(action => action.IsVisible).ToArray();

        public bool HasCurrentStatus =>
            CurrentStatus != AnimeTrackingStatus.None;

        public string CurrentStatusHint
        {
            get
            {
                var action = TrackingActions.FirstOrDefault(candidate =>
                    candidate.Status == CurrentStatus);
                return action is null
                    ? string.Empty
                    : $"当前标记：{action.ActiveLabel}";
            }
        }

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

                var releasePhase = AnimeReleaseClassifier.Classify(
                    AnimeDetail,
                    DateOnly.FromDateTime(DateTime.Today));
                IsCurrentSeason = releasePhase
                    == AnimeReleasePhase.CurrentSeason;
                IsOldSeason = releasePhase == AnimeReleasePhase.Past;
                ReleasePhaseText = AnimeReleaseClassifier.GetPhaseText(
                    releasePhase);
                MediaFormatText = AnimeReleaseClassifier.GetMediaFormatText(
                    AnimeDetail.MediaFormat);

                // 加载详情后查询当前关注状态
                var status = await _trackingService.GetStatusAsync(animeID);
                CurrentStatus = status ?? AnimeTrackingStatus.None;
            }
            catch (HttpRequestException ex)
            {
                ErrorMessage = $"网络请求失败：{ex.Message}";
                IsError = true;
            }
            catch (BangumiApiException ex)
            {
                ErrorMessage = $"数据源请求失败：{ex.Message}";
                IsError = true;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户操作引起的取消，静默忽略
                return;
            }
            catch (TaskCanceledException)
            {
                // HTTP 超时或其他网络层取消，作为错误处理
                ErrorMessage = "网络请求超时，请检查网络后重试";
                IsError = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                ErrorMessage = $"数据解析失败：{ex.Message}";
                IsError = true;
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                    IsLoading = false;
            }
        }

        [RelayCommand]
        private void RetryLoad()
        {
            LoadDetailCommand.Execute(_lastAnimeID);
        }


        /// <summary>统一设置番剧状态：已设置则取消，未设置则设置。</summary>
        [RelayCommand]
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

        partial void OnCurrentStatusChanged(AnimeTrackingStatus value)
        {
            foreach (var action in TrackingActions)
            {
                action.IsSelected = action.Status == value;
            }
        }

        partial void OnIsCurrentSeasonChanged(bool value)
        {
            UpdateTrackingActionAvailability();
        }

        partial void OnIsOldSeasonChanged(bool value)
        {
            UpdateTrackingActionAvailability();
        }

        private void UpdateTrackingActionAvailability()
        {
            foreach (var action in TrackingActions)
            {
                action.UpdateAvailability(IsCurrentSeason, IsOldSeason);
            }

            OnPropertyChanged(nameof(VisibleTrackingActions));
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
