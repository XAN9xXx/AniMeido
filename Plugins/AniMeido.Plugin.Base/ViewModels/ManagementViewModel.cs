using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AniMeido.Plugin.Base.ViewModels
{
    public record TagItem(string TagName, bool IsExpanded);

    public partial class ManagementViewModel : ObservableObject
    {
        private readonly TrackingService _trackingService;
        private readonly IAnimeDataSource _animeDataSource;
        private readonly SavedTagService _savedTagService;
        private readonly Dictionary<AnimeTrackingStatus, IReadOnlyList<int>>
            _statusIdsCache = [];
        private readonly HashSet<AnimeTrackingStatus> _loadedSections = [];
        private CancellationTokenSource? _sectionCancellation;
        private CancellationTokenSource? _tagCancellation;
        private int _loadVersion;
        private int _sectionLoadVersion;
        private int _tagLoadVersion;

        [ObservableProperty]
        private TrackingStatusSection _selectedSection = null!;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _isError;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private ObservableCollection<TagItem> _tagList = [];

        [ObservableProperty]
        private ObservableCollection<Anime> _tagAnimeList = [];

        [ObservableProperty]
        private bool _isTagLoading;

        [ObservableProperty]
        private bool _hasTags;

        [ObservableProperty]
        private string? _tagResultSummary;

        public ObservableCollection<TrackingStatusSection> StatusSections { get; }

        public ManagementViewModel(
            TrackingService trackingService,
            IAnimeDataSource dataSource,
            SavedTagService savedTagService)
        {
            _trackingService = trackingService;
            _animeDataSource = dataSource;
            _savedTagService = savedTagService;
            StatusSections = new(TrackingStatusSection.CreateDefaults());
            SelectedSection = StatusSections[0];
        }

        /// <summary>
        /// 一次读取全部状态 ID 和计数，仅加载当前分区的番剧详情。
        /// </summary>
        [RelayCommand]
        private async Task LoadDataAsync(CancellationToken cancellationToken = default)
        {
            var loadVersion = Interlocked.Increment(ref _loadVersion);
            IsLoading = true;
            ClearError();
            _loadedSections.Clear();
            _statusIdsCache.Clear();

            foreach (var section in StatusSections)
            {
                section.Items.Clear();
                section.Count = 0;
            }

            try
            {
                var idsByStatus =
                    await _trackingService.GetAnimeIdsGroupedByStatusAsync();
                if (loadVersion != _loadVersion
                    || cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                foreach (var section in StatusSections)
                {
                    var ids = idsByStatus.GetValueOrDefault(section.Status) ?? [];
                    _statusIdsCache[section.Status] = ids;
                    section.Count = ids.Count;
                }

                await SelectSectionAsync(
                    SelectedSection,
                    cancellationToken);
            }
            catch (Exception ex) when (HandleLoadException(
                ex,
                cancellationToken))
            {
            }
            finally
            {
                if (loadVersion == _loadVersion)
                {
                    IsLoading = false;
                }
            }
        }

        public async Task SelectSectionAsync(
            TrackingStatusSection section,
            CancellationToken cancellationToken = default)
        {
            _sectionCancellation?.Cancel();
            _sectionCancellation?.Dispose();
            _sectionCancellation = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            var sectionToken = _sectionCancellation.Token;
            var sectionLoadVersion = Interlocked.Increment(
                ref _sectionLoadVersion);
            SelectedSection = section;
            if (_loadedSections.Contains(section.Status))
            {
                return;
            }

            IsLoading = true;
            ClearError();
            try
            {
                var details = await LoadSectionDetailsAsync(
                    section,
                    sectionToken);
                if (sectionLoadVersion != _sectionLoadVersion
                    || sectionToken.IsCancellationRequested)
                {
                    return;
                }
                section.Items.Clear();
                foreach (var anime in details)
                {
                    section.Items.Add(anime);
                }
                _loadedSections.Add(section.Status);
            }
            catch (Exception ex) when (HandleLoadException(
                ex,
                sectionToken))
            {
            }
            finally
            {
                if (sectionLoadVersion == _sectionLoadVersion)
                {
                    IsLoading = false;
                }
            }
        }

        private async Task<IReadOnlyList<Anime>> LoadSectionDetailsAsync(
            TrackingStatusSection section,
            CancellationToken cancellationToken)
        {
            if (_loadedSections.Contains(section.Status))
            {
                return section.Items.ToList();
            }

            if (!_statusIdsCache.TryGetValue(section.Status, out var ids) ||
                ids.Count == 0)
            {
                return [];
            }

            return await LoadAnimeDetailsConcurrentAsync(
                ids,
                cancellationToken);
        }

        [RelayCommand]
        private async Task RemoveFromStatusAsync(Anime anime)
        {
            var section = SelectedSection;

            var removed = await _trackingService.RemoveStatusAsync(anime.ID);
            var item = section.Items.FirstOrDefault(candidate =>
                candidate.ID == anime.ID);
            if (item is not null)
            {
                section.Items.Remove(item);
            }

            if (_statusIdsCache.TryGetValue(section.Status, out var ids))
            {
                _statusIdsCache[section.Status] = ids
                    .Where(id => id != anime.ID)
                    .ToList();
            }

            if (removed)
            {
                section.Count = Math.Max(0, section.Count - 1);
            }
        }

        [RelayCommand]
        private async Task LoadTagsAsync(
            CancellationToken cancellationToken = default)
        {
            IsTagLoading = true;
            ClearError();
            try
            {
                var tags = await _savedTagService.GetAllSavedTagsAsync();
                cancellationToken.ThrowIfCancellationRequested();
                TagList.Clear();
                foreach (var name in tags)
                {
                    TagList.Add(new TagItem(name, false));
                }

                HasTags = TagList.Count > 0;
                TagAnimeList.Clear();
                TagResultSummary = null;
            }
            catch (Exception ex) when (HandleLoadException(
                ex,
                cancellationToken))
            {
            }
            finally
            {
                IsTagLoading = false;
            }
        }

        [RelayCommand]
        private async Task ToggleTagAsync(
            TagItem tag,
            CancellationToken cancellationToken = default)
        {
            _tagCancellation?.Cancel();
            _tagCancellation?.Dispose();
            _tagCancellation = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            var tagToken = _tagCancellation.Token;
            var tagLoadVersion = Interlocked.Increment(ref _tagLoadVersion);
            for (var i = TagList.Count - 1; i >= 0; i--)
            {
                var item = TagList[i];
                if (item != tag && item.IsExpanded)
                {
                    TagList[i] = item with { IsExpanded = false };
                }
            }

            var index = FindTagIndex(tag.TagName);
            if (index < 0)
            {
                return;
            }

            var current = TagList[index];
            var expanded = !current.IsExpanded;
            TagList[index] = current with { IsExpanded = expanded };

            if (!expanded)
            {
                TagAnimeList.Clear();
                TagResultSummary = null;
                return;
            }

            IsTagLoading = true;
            try
            {
                TagAnimeList.Clear();
                var (results, total) = await _animeDataSource.SearchByTagAsync(
                    tag.TagName,
                    0,
                    "rank",
                    tagToken);
                var blocked =
                    await _trackingService.GetBlockedAnimeIdsAsync();
                var currentIndex = FindTagIndex(tag.TagName);
                if (tagLoadVersion != _tagLoadVersion
                    || tagToken.IsCancellationRequested
                    || currentIndex < 0
                    || !TagList[currentIndex].IsExpanded)
                {
                    return;
                }
                TagAnimeList.Clear();
                foreach (var anime in results
                    .Where(item => !blocked.Contains(item.ID))
                    .Take(20))
                {
                    TagAnimeList.Add(anime);
                }
                TagResultSummary = total > TagAnimeList.Count
                    ? $"显示前 {TagAnimeList.Count} 部，共 {total} 部"
                    : $"共 {TagAnimeList.Count} 部";
            }
            catch (Exception ex) when (HandleLoadException(
                ex,
                tagToken))
            {
            }
            finally
            {
                if (tagLoadVersion == _tagLoadVersion)
                {
                    IsTagLoading = false;
                }
            }
        }

        [RelayCommand]
        private async Task DeleteTagAsync(TagItem tag)
        {
            await _savedTagService.RemoveTagAsync(tag.TagName);
            var index = FindTagIndex(tag.TagName);
            if (index >= 0)
            {
                TagList.RemoveAt(index);
            }

            TagAnimeList.Clear();
            TagResultSummary = null;
            HasTags = TagList.Count > 0;
        }

        public void CancelPendingLoads()
        {
            Interlocked.Increment(ref _loadVersion);
            Interlocked.Increment(ref _sectionLoadVersion);
            Interlocked.Increment(ref _tagLoadVersion);
            _sectionCancellation?.Cancel();
            _sectionCancellation?.Dispose();
            _sectionCancellation = null;
            _tagCancellation?.Cancel();
            _tagCancellation?.Dispose();
            _tagCancellation = null;
            LoadDataCommand.Cancel();
            LoadTagsCommand.Cancel();
            ToggleTagCommand.Cancel();
        }

        private int FindTagIndex(string tagName)
        {
            for (var i = TagList.Count - 1; i >= 0; i--)
            {
                if (TagList[i].TagName == tagName)
                {
                    return i;
                }
            }

            return -1;
        }

        private bool HandleLoadException(
            Exception exception,
            CancellationToken cancellationToken)
        {
            switch (exception)
            {
                case OperationCanceledException
                    when cancellationToken.IsCancellationRequested:
                    return true;
                case TaskCanceledException:
                    ErrorMessage = "网络请求超时，请检查网络后重试";
                    break;
                case HttpRequestException:
                    ErrorMessage = $"网络请求失败：{exception.Message}";
                    break;
                case BangumiApiException:
                    ErrorMessage = $"数据源请求失败：{exception.Message}";
                    break;
                case InvalidOperationException:
                case JsonException:
                    ErrorMessage = $"数据解析失败：{exception.Message}";
                    break;
                default:
                    return false;
            }

            IsError = true;
            return true;
        }

        private void ClearError()
        {
            IsError = false;
            ErrorMessage = null;
        }

        private async Task<List<Anime>> LoadAnimeDetailsConcurrentAsync(
            IReadOnlyList<int> ids,
            CancellationToken cancellationToken)
        {
            var results = new ConcurrentBag<Anime>();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken,
            };

            await Parallel.ForEachAsync(
                ids,
                parallelOptions,
                async (id, token) =>
                {
                    try
                    {
                        var anime = await _animeDataSource
                            .GetAnimeDetailAsync(id, token);
                        if (anime is not null)
                        {
                            results.Add(anime);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (HttpRequestException)
                    {
                    }
                    catch (Exception ex) when (
                        ex is InvalidOperationException or JsonException)
                    {
                    }
                });

            return results.ToList();
        }
    }
}
