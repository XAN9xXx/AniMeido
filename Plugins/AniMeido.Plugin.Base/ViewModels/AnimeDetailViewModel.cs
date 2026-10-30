using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Base.ViewModels
{
    public partial class AnimeDetailViewModel : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DescriptionText))]
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
        private bool _isUpcoming = false;
        [ObservableProperty]
        private string _releasePhaseText = "日期未知";
        [ObservableProperty]
        private string _mediaFormatText = "其他动画";
        [ObservableProperty]
        private string? _studiosText = null;
        [ObservableProperty]
        private string _ratingText = "";

        public string DescriptionText => AnimeDetail switch
        {
            null => string.Empty,
            { } anime when PersonalAnimeDataSource.IsUnavailable(anime) => "资料不可用",
            { } anime => anime.Description,
        };

        public ObservableCollection<CharacterRole> Characters { get; private set; } = new();

        private int _lastAnimeID;
        private readonly IAnimeDataSource _animeDataSource;
        private readonly TrackingService _trackingService;
        private readonly MetadataDataSource _ratingSource;
        private readonly ILogger _logger;
        private int _loadVersion;

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

        public AnimeDetailViewModel(
            IAnimeDataSource dataSource,
            TrackingService trackingService,
            MetadataDataSource ratingSource,
            ILogger logger)
        {
            _animeDataSource = dataSource;
            _trackingService = trackingService;
            _ratingSource = ratingSource;
            _logger = logger;
        }



        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task LoadDetailAsync(int animeID, CancellationToken ct = default)
        {
            var loadVersion = Interlocked.Increment(ref _loadVersion);
            IsLoading = true;
            IsError = false;
            ErrorMessage = null;
            HasData = false;
            AnimeDetail = null;
            StudiosText = null;
            RatingText = "";
            Characters.Clear();
            _lastAnimeID = animeID;
            try
            {
                var animeDetail = await _animeDataSource.GetAnimeDetailAsync(
                    animeID,
                    ct);
                if (!IsCurrentLoad(loadVersion, ct))
                {
                    return;
                }

                AnimeDetail = animeDetail;
                if (animeDetail is null)
                {
                    HasData = false;
                    IsError = true;
                    ErrorMessage = "未找到该番剧或数据暂不可用。";
                    return;
                }

                HasData = true;

                await Task.WhenAll(
                    LoadStudiosAsync(animeID, loadVersion, ct),
                    LoadCharactersAsync(animeID, loadVersion, ct),
                    LoadRatingsAsync(animeDetail, loadVersion, ct)
                );
                if (!IsCurrentLoad(loadVersion, ct))
                {
                    return;
                }

                var releasePhase = AnimeReleaseClassifier.Classify(
                    animeDetail,
                    DateOnly.FromDateTime(DateTime.Today));
                IsCurrentSeason = releasePhase
                    == AnimeReleasePhase.CurrentSeason;
                IsOldSeason = releasePhase == AnimeReleasePhase.Past;
                IsUpcoming = releasePhase == AnimeReleasePhase.Upcoming;
                ReleasePhaseText = AnimeReleaseClassifier.GetPhaseText(
                    releasePhase);
                MediaFormatText = AnimeReleaseClassifier.GetMediaFormatText(
                    animeDetail.MediaFormat);

                var status = await _trackingService.GetStatusAsync(animeID);
                if (!IsCurrentLoad(loadVersion, ct))
                {
                    return;
                }

                CurrentStatus = status ?? AnimeTrackingStatus.None;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is HttpRequestException
                or MetadataServiceException or TaskCanceledException
                or InvalidOperationException or JsonException)
            {
                if (!IsCurrentLoad(loadVersion, ct))
                {
                    return;
                }

                _logger.LogError(ex, "Anime detail load failed for {AnimeId}.", animeID);
                ErrorMessage = ex switch
                {
                    HttpRequestException => $"网络请求失败：{ex.Message}",
                    MetadataServiceException => $"数据源请求失败：{ex.Message}",
                    TaskCanceledException => "网络请求超时，请检查网络后重试",
                    _ => $"数据解析失败：{ex.Message}",
                };
                IsError = true;
            }
            finally
            {
                if (loadVersion == _loadVersion)
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

        partial void OnIsUpcomingChanged(bool value)
        {
            UpdateTrackingActionAvailability();
        }

        private void UpdateTrackingActionAvailability()
        {
            // 未上映的作品同样可以“追番”：AnimeTrackingStatus.Watching 的语义
            // 覆盖“正在追”和“未上映，计划追”两种情况。
            foreach (var action in TrackingActions)
            {
                action.UpdateAvailability(
                    IsCurrentSeason || IsUpcoming,
                    IsOldSeason);
            }

            OnPropertyChanged(nameof(VisibleTrackingActions));
        }

        private async Task LoadStudiosAsync(
            int animeID,
            int loadVersion,
            CancellationToken cancellationToken)
        {
            try
            {
                var studios = await _animeDataSource.GetStudioAsync(
                    animeID,
                    cancellationToken);
                if (!IsCurrentLoad(loadVersion, cancellationToken))
                {
                    return;
                }

                StudiosText = studios.Count > 0
                    ? $"制作/原作：{string.Join("、", studios.Select(s => s.Name))}"
                    : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or MetadataServiceException)
            {
                if (IsCurrentLoad(loadVersion, cancellationToken))
                    _logger.LogWarning(ex, "Studio information unavailable for {AnimeId}; keeping the main detail.", animeID);
            }
        }

        private async Task LoadCharactersAsync(
            int animeID,
            int loadVersion,
            CancellationToken cancellationToken)
        {
            try
            {
                var characters = await _animeDataSource.GetCharacterRolesAsync(
                    animeID,
                    cancellationToken);
                if (!IsCurrentLoad(loadVersion, cancellationToken))
                {
                    return;
                }

                Characters.Clear();
                foreach (var c in characters)
                    Characters.Add(c);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or MetadataServiceException)
            {
                if (IsCurrentLoad(loadVersion, cancellationToken))
                    _logger.LogWarning(ex, "Character information unavailable for {AnimeId}; keeping the main detail.", animeID);
            }
        }

        private async Task LoadRatingsAsync(Anime anime, int loadVersion, CancellationToken ct)
        {
            if (PersonalAnimeDataSource.IsUnavailable(anime)) return;
            var text = await _ratingSource.GetRatingTextAsync(anime.ID, ct);
            if (IsCurrentLoad(loadVersion, ct)) RatingText = text;
        }

        private bool IsCurrentLoad(
            int loadVersion,
            CancellationToken cancellationToken)
            => !cancellationToken.IsCancellationRequested
                && loadVersion == _loadVersion;
    }
}
