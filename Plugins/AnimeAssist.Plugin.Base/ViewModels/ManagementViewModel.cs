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
                foreach (var section in StatusSections)
                {
                    var ids = idsByStatus.GetValueOrDefault(section.Status) ?? [];
                    _statusIdsCache[section.Status] = ids;
                    section.Count = ids.Count;
                }

                await LoadSectionCoreAsync(
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
                IsLoading = false;
            }
        }

        public async Task SelectSectionAsync(
            TrackingStatusSection section,
            CancellationToken cancellationToken = default)
        {
            SelectedSection = section;
            if (_loadedSections.Contains(section.Status))
            {
                return;
            }

            IsLoading = true;
            ClearError();
            try
            {
                await LoadSectionCoreAsync(section, cancellationToken);
            }
            catch (Exception ex) when (HandleLoadException(
                ex,
                cancellationToken))
            {
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task LoadSectionCoreAsync(
            TrackingStatusSection section,
            CancellationToken cancellationToken)
        {
            if (_loadedSections.Contains(section.Status))
            {
                return;
            }

            if (!_statusIdsCache.TryGetValue(section.Status, out var ids) ||
                ids.Count == 0)
            {
                _loadedSections.Add(section.Status);
                return;
            }

            var details = await LoadAnimeDetailsConcurrentAsync(
                ids,
                cancellationToken);
            section.Items.Clear();
            foreach (var anime in details)
            {
                section.Items.Add(anime);
            }

            _loadedSections.Add(section.Status);
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
        private async Task LoadTagsAsync()
        {
            IsTagLoading = true;
            ClearError();
            try
            {
                var tags = await _savedTagService.GetAllSavedTagsAsync();
                TagList.Clear();
                foreach (var name in tags)
                {
                    TagList.Add(new TagItem(name, false));
                }

                HasTags = TagList.Count > 0;
                TagAnimeList.Clear();
            }
            finally
            {
                IsTagLoading = false;
            }
        }

        [RelayCommand]
        private async Task ToggleTagAsync(TagItem tag)
        {
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
                return;
            }

            IsTagLoading = true;
            try
            {
                TagAnimeList.Clear();
                var (results, _) = await _animeDataSource.SearchByTagAsync(
                    tag.TagName,
                    0,
                    "rank",
                    CancellationToken.None);
                var blocked =
                    await _trackingService.GetBlockedAnimeIdsAsync();
                foreach (var anime in results
                    .Where(item => !blocked.Contains(item.ID))
                    .Take(20))
                {
                    TagAnimeList.Add(anime);
                }
            }
            catch (Exception ex) when (HandleLoadException(
                ex,
                CancellationToken.None))
            {
            }
            finally
            {
                IsTagLoading = false;
            }
        }

        [RelayCommand]
        private async Task DeleteTagAsync(TagItem tag)
        {
            var index = FindTagIndex(tag.TagName);
            if (index >= 0)
            {
                TagList.RemoveAt(index);
            }

            TagAnimeList.Clear();
            HasTags = TagList.Count > 0;
            await _savedTagService.RemoveTagAsync(tag.TagName);
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
                case TaskCanceledException
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
